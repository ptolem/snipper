namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Wave 4 framework evidence: identifies members that frameworks invoke without
/// any C# reference — serialization DTO graphs (STJ source-gen contexts, Refit
/// signatures, [FromBody] binding, serializer call sites, GraphQL response
/// contracts), DI/framework-registered contract implementations (health checks,
/// exception handlers, Refit clients), ASP.NET middleware conventions, and
/// FluentValidation validators. Doctrine: err toward "used" — this evidence only
/// ever suppresses findings, never creates them. Evidence is gathered from ALL
/// documents (generated/external code included): it is never a finding location.
/// </summary>
internal sealed class FrameworkEvidenceIndex
{
    private static readonly ConditionalWeakTable<Solution, Lazy<FrameworkEvidenceIndex>> Cache = new();

    // JsonSerializer/JsonConvert generic methods (matched with the receiver name).
    private static readonly FrozenSet<string> SerializerGenericMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "Deserialize",
        "Serialize",
        "DeserializeAsync",
        "SerializeAsync",
    }.ToFrozenSet(StringComparer.Ordinal);

    // HttpClient JSON extension methods (matched on any receiver).
    private static readonly FrozenSet<string> JsonExtensionMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "ReadFromJsonAsync",
        "GetFromJsonAsync",
        "PostAsJsonAsync",
        "PutAsJsonAsync",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Refit HTTP verb attributes — bare names; ASP.NET uses HttpGet-prefixed names.
    private static readonly FrozenSet<string> RefitAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Get",
        "Post",
        "Put",
        "Delete",
        "Patch",
        "Head",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> BindingAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "FromBody",
        "FromForm",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> GraphContractNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "IGraphQueryRequest",
        "IGraphQLRequest",
    }.ToFrozenSet(StringComparer.Ordinal);

    // DI/framework registration shapes whose type argument is instantiated and
    // called through its contracts by the framework itself.
    private static readonly FrozenSet<string> RegistrationMethodNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "AddScoped",
        "AddTransient",
        "AddSingleton",
        "AddHostedService",
        "TryAddScoped",
        "TryAddTransient",
        "TryAddSingleton",
        "AddCheck",
        "AddTypeActivatedCheck",
        "AddRefitClient",
        "AddExceptionHandler",
        "AddSchemaFilter",
        "AddDocumentTransformer",
        "AddOperationTransformer",
        "AddSchemaTransformer",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> MemberSerializationAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "JsonPropertyName",
        "JsonProperty",
        "JsonInclude",
        "JsonConstructor",
        "JsonExtensionData",
        "BsonElement",
        "BsonId",
        "BsonDiscriminator",
        "DataMember",
        "ProtoMember",
        "XmlElement",
        "XmlAttribute",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenSet<INamedTypeSymbol> _serializationUsedTypes;
    private readonly FrozenSet<INamedTypeSymbol> _frameworkRegisteredTypes;

    private FrameworkEvidenceIndex(
        FrozenSet<INamedTypeSymbol> serializationUsedTypes,
        FrozenSet<INamedTypeSymbol> frameworkRegisteredTypes)
    {
        _serializationUsedTypes = serializationUsedTypes;
        _frameworkRegisteredTypes = frameworkRegisteredTypes;
    }

    public static FrameworkEvidenceIndex Get(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return Cache.GetValue(
                solution,
                static s => new Lazy<FrameworkEvidenceIndex>(() => Build(s), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// True when the member is reachable through a framework channel and must not
    /// be flagged, even with zero C# references.
    /// </summary>
    public bool IsUsed(ISymbol member)
    {
        ArgumentNullException.ThrowIfNull(member);

        foreach (var attribute in member.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass
                && MemberSerializationAttributes.Contains(StripAttributeSuffix(attributeClass.Name)))
            {
                return true;
            }
        }

        // Type candidates query on themselves; member candidates on their container.
        var type = (member as INamedTypeSymbol ?? member.ContainingType)?.OriginalDefinition;
        if (type is null)
        {
            return false;
        }

        if (_serializationUsedTypes.Contains(type))
        {
            return true;
        }

        // A framework-registered type is constructed by the container; its members
        // are framework-called only when they implement a contract.
        if (_frameworkRegisteredTypes.Contains(type)
            && (member is INamedTypeSymbol || ImplementsAnyInterfaceMember(member)))
        {
            return true;
        }

        if (member is IMethodSymbol { Name: "Invoke" or "InvokeAsync" }
            && type.Name.EndsWith("Middleware", StringComparison.Ordinal))
        {
            return true;
        }

        return DerivesFromAbstractValidator(type);
    }

    private static FrameworkEvidenceIndex Build(Solution solution)
    {
        var serializationSeeds = new List<(Document Document, TypeSyntax Type)>();
        var registrationSeeds = new List<(Document Document, TypeSyntax Type)>();

        // Pass 1: syntax seeds. No semantic calls — safe to read every document.
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (root is null)
                {
                    continue;
                }

                CollectSeeds(document, root, serializationSeeds, registrationSeeds);
            }
        }

        // Pass 2: resolve seed types. Sequential binding: workspace compilations are
        // built with ConcurrentBuild=false. Only seed-bearing documents need a model.
        var seedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var registeredTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        ResolveSeeds(serializationSeeds, seedTypes, normalizeToDefinition: false);
        ResolveSeeds(registrationSeeds, registeredTypes, normalizeToDefinition: true);

        // Pass 3: DTO closure — public instance property/field types and generic
        // type arguments, transitively (handles Task<Dto>, IApiResponse<Dto>, and
        // nested response graphs). Generic wrappers are expanded BEFORE definition
        // normalization so Task<Dto> still reaches Dto; only source types enter
        // the closure itself (metadata expansions are noise, and no candidate's
        // containing type is metadata).
        var closure = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>(seedTypes);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();

            foreach (var typeArgument in type.TypeArguments)
            {
                if (typeArgument is INamedTypeSymbol namedArgument)
                {
                    queue.Enqueue(namedArgument);
                }
            }

            var definition = type.OriginalDefinition;
            if (definition.DeclaringSyntaxReferences.Length == 0 || !closure.Add(definition))
            {
                continue;
            }

            foreach (var member in definition.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false } property && property.Type is INamedTypeSymbol propertyType)
                {
                    queue.Enqueue(propertyType);
                }
                else if (member is IFieldSymbol { IsStatic: false } field && field.Type is INamedTypeSymbol fieldType)
                {
                    queue.Enqueue(fieldType);
                }
            }
        }

        return new FrameworkEvidenceIndex(
            closure.ToFrozenSet((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default),
            registeredTypes.ToFrozenSet((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default));
    }

    private static void CollectSeeds(
        Document document,
        SyntaxNode root,
        List<(Document, TypeSyntax)> serializationSeeds,
        List<(Document, TypeSyntax)> registrationSeeds)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AttributeSyntax attribute when IsJsonSerializableAttribute(attribute):
                {
                    foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
                    {
                        if (argument.Expression is TypeOfExpressionSyntax typeOfExpression)
                        {
                            serializationSeeds.Add((document, typeOfExpression.Type));
                        }
                    }

                    break;
                }

                case InvocationExpressionSyntax invocation:
                    CollectInvocationSeeds(document, invocation, serializationSeeds, registrationSeeds);
                    break;

                case ParameterSyntax { AttributeLists.Count: > 0 } parameter when HasBindingAttribute(parameter) && parameter.Type is not null:
                    serializationSeeds.Add((document, parameter.Type!));
                    break;

                case InterfaceDeclarationSyntax interfaceDeclaration:
                    CollectRefitSeeds(document, interfaceDeclaration, serializationSeeds);
                    break;

                case TypeDeclarationSyntax { BaseList: not null } typeDeclaration:
                    CollectGraphContractSeeds(document, typeDeclaration, serializationSeeds);
                    break;
            }
        }
    }

    private static void CollectInvocationSeeds(
        Document document,
        InvocationExpressionSyntax invocation,
        List<(Document, TypeSyntax)> serializationSeeds,
        List<(Document, TypeSyntax)> registrationSeeds)
    {
        var (name, receiverText, genericName) = invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => (memberAccess.Name, memberAccess.Expression.ToString(), memberAccess.Name as GenericNameSyntax),
            MemberBindingExpressionSyntax memberBinding => (memberBinding.Name, string.Empty, memberBinding.Name as GenericNameSyntax),
            _ => ((SimpleNameSyntax?)null, string.Empty, null),
        };

        if (name is null)
        {
            return;
        }

        var identifier = name.Identifier.Text;
        var isSerializerCall =
            (SerializerGenericMethods.Contains(identifier)
                && (receiverText.EndsWith("JsonSerializer", StringComparison.Ordinal) || receiverText.EndsWith("JsonConvert", StringComparison.Ordinal)))
            || JsonExtensionMethods.Contains(identifier)
            || (identifier == "RegisterClassMap" && receiverText.EndsWith("BsonClassMap", StringComparison.Ordinal));

        if (isSerializerCall && genericName is not null)
        {
            foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
            {
                serializationSeeds.Add((document, typeArgument));
            }
        }

        if (!RegistrationMethodNames.Contains(identifier))
        {
            return;
        }

        if (genericName is not null)
        {
            foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
            {
                registrationSeeds.Add((document, typeArgument));
            }
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is TypeOfExpressionSyntax typeOfExpression)
            {
                registrationSeeds.Add((document, typeOfExpression.Type));
            }
        }
    }

    private static void CollectRefitSeeds(
        Document document,
        InterfaceDeclarationSyntax interfaceDeclaration,
        List<(Document, TypeSyntax)> serializationSeeds)
    {
        foreach (var method in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            var isRefitMethod = method.AttributeLists
                .SelectMany(static list => list.Attributes)
                .Any(static attribute => RefitAttributeNames.Contains(StripAttributeSuffix(attribute.Name.ToString())));

            if (!isRefitMethod)
            {
                continue;
            }

            serializationSeeds.Add((document, method.ReturnType));
            foreach (var parameter in method.ParameterList.Parameters)
            {
                if (parameter.Type is not null)
                {
                    serializationSeeds.Add((document, parameter.Type));
                }
            }
        }
    }

    private static void CollectGraphContractSeeds(
        Document document,
        TypeDeclarationSyntax typeDeclaration,
        List<(Document, TypeSyntax)> serializationSeeds)
    {
        foreach (var genericName in typeDeclaration.BaseList!.DescendantNodes().OfType<GenericNameSyntax>())
        {
            if (!GraphContractNames.Contains(genericName.Identifier.Text))
            {
                continue;
            }

            foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
            {
                serializationSeeds.Add((document, typeArgument));
            }
        }
    }

    private static void ResolveSeeds(
        List<(Document Document, TypeSyntax Type)> seeds,
        HashSet<INamedTypeSymbol> resolved,
        bool normalizeToDefinition)
    {
        foreach (var group in seeds.GroupBy(static seed => seed.Document))
        {
            var semanticModel = group.Key.GetSemanticModelAsync().GetAwaiter().GetResult();
            if (semanticModel is null)
            {
                continue;
            }

            foreach (var (_, typeSyntax) in group)
            {
                if (semanticModel.GetTypeInfo(typeSyntax).Type is INamedTypeSymbol namedType)
                {
                    resolved.Add(normalizeToDefinition ? namedType.OriginalDefinition : namedType);
                }
            }
        }
    }

    private static bool ImplementsAnyInterfaceMember(ISymbol member)
    {
        if (member is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
            || member is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 })
        {
            return true;
        }

        var containingType = member.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        foreach (var contract in containingType.AllInterfaces)
        {
            foreach (var contractMember in contract.GetMembers())
            {
                if (SymbolEqualityComparer.Default.Equals(
                        containingType.FindImplementationForInterfaceMember(contractMember),
                        member))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool DerivesFromAbstractValidator(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == "AbstractValidator")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsJsonSerializableAttribute(AttributeSyntax attribute)
    {
        return StripAttributeSuffix(attribute.Name.ToString()) == "JsonSerializable";
    }

    private static bool HasBindingAttribute(ParameterSyntax parameter)
    {
        return parameter.AttributeLists
            .SelectMany(static list => list.Attributes)
            .Any(static attribute => BindingAttributeNames.Contains(StripAttributeSuffix(attribute.Name.ToString())));
    }

    private static string StripAttributeSuffix(string name)
    {
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }
}
