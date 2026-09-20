namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Wave 4 framework evidence: identifies members that frameworks invoke without
/// any C# reference — serialization DTO graphs (STJ source-gen contexts, Refit
/// signatures, [FromBody] binding, serializer call sites, GraphQL response
/// contracts), framework-dispatched contract implementations (health checks,
/// FusionCache serializers, hosted services, exception handlers, OpenAPI
/// filters/transformers, xUnit/MVC/MediatR dispatch), reflection plugin-by-scan
/// types (IsSubclassOf/IsAssignableFrom discovery), ASP.NET middleware
/// conventions, and FluentValidation validators. Plain DI registration is NOT
/// evidence: calls through a registered contract are ordinary C# references the
/// reference graph already sees, so an uncalled contract member on a registered
/// type is still dead code. Doctrine: err toward "used" — this evidence only
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

    // Contracts the framework itself dispatches, invisibly to the reference
    // graph: health checks, FusionCache serializers, hosted services, ASP.NET
    // exception handlers, Swashbuckle/OpenAPI filters/transformers/examples,
    // xUnit serialization/orderers, the MVC filter family, and MediatR pipeline
    // middleware. Matched by simple name, the same convention as
    // AbstractValidator below. Note what is deliberately absent: ordinary
    // application contracts. A type registered via AddScoped/AddSingleton/etc.
    // is called through its contract by consumer C# — those calls are visible,
    // so the reference graph stays the arbiter. (MediatR request/notification
    // HANDLERS stay out on that rule: they are directly callable, and milkrun
    // tests call them. Pipeline behaviours are middleware — never directly
    // callable, always runtime-dispatched.)
    private static readonly FrozenSet<string> FrameworkDispatchedContracts = new HashSet<string>(StringComparer.Ordinal)
    {
        "IHealthCheck",
        "IFusionCacheSerializer",
        "IHostedService",
        "IExceptionHandler",
        "ISchemaFilter",
        "IDocumentTransformer",
        "IOperationTransformer",
        "ISchemaTransformer",
        "IOpenApiDocumentTransformer",
        "IOpenApiOperationTransformer",
        "IOpenApiSchemaTransformer",
        "IDocumentFilter",
        "IOperationFilter",
        "IExamplesProvider",
        "IXunitSerializable",
        "ITestCaseOrderer",
        "IXunitTestCaseOrderer",
        "IActionFilter",
        "IAsyncActionFilter",
        "IOrderedFilter",
        "IExceptionFilter",
        "IAsyncExceptionFilter",
        "IResultFilter",
        "IAsyncResultFilter",
        "IResourceFilter",
        "IAsyncResourceFilter",
        "IAuthorizationFilter",
        "IAsyncAuthorizationFilter",
        "IPipelineBehavior",
        "IStreamPipelineBehavior",
        "IRequestExceptionHandler",
        "IRequestPreProcessor",
        "IRequestPostProcessor",
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
    private readonly FrozenSet<INamedTypeSymbol> _reflectionDiscoveredTypes;

    private FrameworkEvidenceIndex(
        FrozenSet<INamedTypeSymbol> serializationUsedTypes,
        FrozenSet<INamedTypeSymbol> reflectionDiscoveredTypes)
    {
        _serializationUsedTypes = serializationUsedTypes;
        _reflectionDiscoveredTypes = reflectionDiscoveredTypes;
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
    /// True when the member carries a serialization attribute — including
    /// <c>[JsonInclude]</c>, which lets STJ read and write <em>non-public</em>
    /// members. Used by SNP0001 for private candidates; the broader
    /// <see cref="IsUsed"/> query (DTO closure, dispatched contracts,
    /// conventions) stays with SNP0005/0006.
    /// </summary>
    public static bool HasMemberSerializationAttribute(ISymbol member)
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

        return false;
    }

    /// <summary>
    /// True when the member is reachable through a framework channel and must not
    /// be flagged, even with zero C# references.
    /// </summary>
    public bool IsUsed(ISymbol member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (HasMemberSerializationAttribute(member))
        {
            return true;
        }

        // Type-level evidence, type candidates only (members still stand on
        // their own reference counts — the framework instantiates the type,
        // nothing more):
        //  - Reflection plugin-by-scan: a base type spelled in an IsSubclassOf
        //    call, or as the receiver of IsAssignableFrom, marks its derived
        //    types as Activator-instantiated (assembly scan discovery).
        //  - Framework-dispatched contract implementations: the framework
        //    activates the implementation itself (generic registration,
        //    assembly scan, DI activation) — 1.5.1 suppressed only the
        //    contract members; 1.6.2 extends that to the implementation type
        //    after milkrun showed scan-instantiated example/filter types
        //    flagged with zero C# references.
        if (member is INamedTypeSymbol typeCandidate
            && (_reflectionDiscoveredTypes.Contains(typeCandidate.OriginalDefinition)
                || ImplementsFrameworkContract(typeCandidate)))
        {
            return true;
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

        // A member implementing a framework-dispatched contract is invoked by
        // the framework itself — no C# reference can exist for it.
        if (ImplementsFrameworkContractMember(member))
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
        var serializationSeeds = new ConcurrentBag<(Document Document, TypeSyntax Type)>();
        var scanSeeds = new ConcurrentBag<(Document Document, InvocationExpressionSyntax Invocation, string BaseName)>();
        var classRegistrations = new ConcurrentBag<(Document Document, TypeDeclarationSyntax Declaration, string Name, IReadOnlyList<string> BaseNames)>();

        // Pass 1: syntax seeds. No semantic calls — safe to read every document,
        // and safe to parallelise (order is irrelevant: seeds feed sets).
        var documents = solution.Projects
            .SelectMany(static p => p.Documents)
            .Where(static d => d.SupportsSyntaxTree);
        Parallel.ForEach(
            documents,
            AnalysisParallelism.CreateOptions(CancellationToken.None),
            document =>
            {
                var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                if (root is null)
                {
                    return;
                }

                CollectSeeds(document, root, serializationSeeds, scanSeeds, classRegistrations);
            });

        // Pass 2: resolve seed types. Per-document semantic binding parallelised
        // behind the revertible switch (spike-proven drift-free, 2026-09-18);
        // SNIPPER_MAX_DOP=1 restores the sequential path. Only seed-bearing
        // documents need a model.
        var seedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        ResolveSeeds(serializationSeeds, seedTypes, normalizeToDefinition: false);

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

        // Pass 4: reflection plugin-by-scan. Confirm the IsSubclassOf/
        // IsAssignableFrom call sites semantically, close over the syntax-level
        // base map by simple name (transitively — a scan discovers subclasses of
        // subclasses), then resolve only the discovered declarations.
        var scanBaseNames = ResolveScanBaseNames(scanSeeds);
        var discoveredNames = CloseDiscoveredNames(scanBaseNames, classRegistrations);
        var reflectionDiscoveredTypes = ResolveDiscoveredTypes(discoveredNames, classRegistrations);

        return new FrameworkEvidenceIndex(
            closure.ToFrozenSet((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default),
            reflectionDiscoveredTypes);
    }

    private static void CollectSeeds(
        Document document,
        SyntaxNode root,
        ConcurrentBag<(Document, TypeSyntax)> serializationSeeds,
        ConcurrentBag<(Document, InvocationExpressionSyntax, string)> scanSeeds,
        ConcurrentBag<(Document, TypeDeclarationSyntax, string, IReadOnlyList<string>)> classRegistrations)
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
                    CollectInvocationSeeds(document, invocation, serializationSeeds);
                    CollectScanSeed(document, invocation, scanSeeds);
                    break;

                case ParameterSyntax { AttributeLists.Count: > 0 } parameter when HasBindingAttribute(parameter) && parameter.Type is not null:
                    serializationSeeds.Add((document, parameter.Type!));
                    break;

                case InterfaceDeclarationSyntax interfaceDeclaration:
                    CollectRefitSeeds(document, interfaceDeclaration, serializationSeeds);
                    break;

                case TypeDeclarationSyntax { BaseList: not null } typeDeclaration:
                    CollectGraphContractSeeds(document, typeDeclaration, serializationSeeds);
                    CollectClassRegistration(document, typeDeclaration, classRegistrations);
                    break;
            }
        }
    }

    private static void CollectInvocationSeeds(
        Document document,
        InvocationExpressionSyntax invocation,
        ConcurrentBag<(Document, TypeSyntax)> serializationSeeds)
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

        if (!isSerializerCall || genericName is null)
        {
            return;
        }

        foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
        {
            serializationSeeds.Add((document, typeArgument));
        }
    }

    private static void CollectRefitSeeds(
        Document document,
        InterfaceDeclarationSyntax interfaceDeclaration,
        ConcurrentBag<(Document, TypeSyntax)> serializationSeeds)
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
        ConcurrentBag<(Document, TypeSyntax)> serializationSeeds)
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

    /// <summary>
    /// Reflection plugin-by-scan seeds: <c>t.IsSubclassOf(typeof(T))</c> and
    /// <c>typeof(T).IsAssignableFrom(candidate)</c> mark T as a scan base.
    /// Syntax-first; the invocation is confirmed semantically in
    /// <see cref="ResolveScanBaseNames"/>.
    /// </summary>
    private static void CollectScanSeed(
        Document document,
        InvocationExpressionSyntax invocation,
        ConcurrentBag<(Document, InvocationExpressionSyntax, string)> scanSeeds)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        var scanBase = memberAccess.Name.Identifier.Text switch
        {
            "IsSubclassOf" when invocation.ArgumentList.Arguments is [{ Expression: TypeOfExpressionSyntax argumentTypeOf }] => argumentTypeOf.Type,
            "IsAssignableFrom" when memberAccess.Expression is TypeOfExpressionSyntax receiverTypeOf => receiverTypeOf.Type,
            _ => null,
        };

        if (scanBase is not null && SimpleNameOf(scanBase) is { } baseName)
        {
            scanSeeds.Add((document, invocation, baseName));
        }
    }

    /// <summary>
    /// Every type declaration with a base list, with its base types reduced to
    /// simple names — the syntax-level map the scan-base closure runs over.
    /// </summary>
    private static void CollectClassRegistration(
        Document document,
        TypeDeclarationSyntax typeDeclaration,
        ConcurrentBag<(Document, TypeDeclarationSyntax, string, IReadOnlyList<string>)> classRegistrations)
    {
        var baseNames = new List<string>();
        foreach (var baseType in typeDeclaration.BaseList!.Types)
        {
            if (SimpleNameOf(baseType.Type) is { } baseName)
            {
                baseNames.Add(baseName);
            }
        }

        if (baseNames.Count > 0)
        {
            classRegistrations.Add((document, typeDeclaration, typeDeclaration.Identifier.Text, baseNames));
        }
    }

    private static string? SimpleNameOf(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => SimpleNameOf(qualified.Right),
            AliasQualifiedNameSyntax alias => SimpleNameOf(alias.Name),
            NullableTypeSyntax nullable => SimpleNameOf(nullable.ElementType),
            _ => null,
        };
    }

    /// <summary>
    /// Confirms scan call sites bind to <c>System.Type</c> (a same-named
    /// extension/helper method is not a reflection scan). Over-approximation on
    /// unresolvable shapes — drifted compilations, missing references: the
    /// named base still counts, per the err-toward-used doctrine.
    /// </summary>
    private static HashSet<string> ResolveScanBaseNames(
        ConcurrentBag<(Document Document, InvocationExpressionSyntax Invocation, string BaseName)> scanSeeds)
    {
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        if (scanSeeds.IsEmpty)
        {
            return confirmed;
        }

        var sync = new object();
        Parallel.ForEach(
            scanSeeds.GroupBy(static seed => seed.Document),
            AnalysisParallelism.CreateOptions(CancellationToken.None),
            group =>
            {
                var semanticModel = group.Key.GetSemanticModelAsync().GetAwaiter().GetResult();
                foreach (var (_, invocation, baseName) in group)
                {
                    var method = semanticModel?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (method is not null
                        && !(method.ContainingType is { Name: "Type" }
                            && method.ContainingType.ContainingNamespace?.ToDisplayString() == "System"))
                    {
                        continue;
                    }

                    lock (sync)
                    {
                        confirmed.Add(baseName);
                    }
                }
            });

        return confirmed;
    }

    /// <summary>
    /// Simple-name closure: a type whose base list names a scan base — or names
    /// an already-discovered type — is discovered too (a scan finds subclasses
    /// transitively). Simple names, the FrameworkDispatchedContracts convention.
    /// </summary>
    private static HashSet<string> CloseDiscoveredNames(
        HashSet<string> scanBaseNames,
        ConcurrentBag<(Document Document, TypeDeclarationSyntax Declaration, string Name, IReadOnlyList<string> BaseNames)> classRegistrations)
    {
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        if (scanBaseNames.Count == 0)
        {
            return discovered;
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (_, _, name, baseNames) in classRegistrations)
            {
                if (discovered.Contains(name))
                {
                    continue;
                }

                foreach (var baseName in baseNames)
                {
                    if (scanBaseNames.Contains(baseName) || discovered.Contains(baseName))
                    {
                        discovered.Add(name);
                        changed = true;
                        break;
                    }
                }
            }
        }

        return discovered;
    }

    private static FrozenSet<INamedTypeSymbol> ResolveDiscoveredTypes(
        HashSet<string> discoveredNames,
        ConcurrentBag<(Document Document, TypeDeclarationSyntax Declaration, string Name, IReadOnlyList<string> BaseNames)> classRegistrations)
    {
        var resolved = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        if (discoveredNames.Count == 0)
        {
            return resolved.ToFrozenSet((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default);
        }

        var sync = new object();
        Parallel.ForEach(
            classRegistrations
                .Where(registration => discoveredNames.Contains(registration.Name))
                .GroupBy(static registration => registration.Document),
            AnalysisParallelism.CreateOptions(CancellationToken.None),
            group =>
            {
                var semanticModel = group.Key.GetSemanticModelAsync().GetAwaiter().GetResult();
                if (semanticModel is null)
                {
                    return;
                }

                foreach (var (_, declaration, _, _) in group)
                {
                    if (semanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol type)
                    {
                        lock (sync)
                        {
                            resolved.Add(type.OriginalDefinition);
                        }
                    }
                }
            });

        return resolved.ToFrozenSet((IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default);
    }

    private static void ResolveSeeds(
        ConcurrentBag<(Document Document, TypeSyntax Type)> seeds,
        HashSet<INamedTypeSymbol> resolved,
        bool normalizeToDefinition)
    {
        var sync = new object();
        Parallel.ForEach(
            seeds.GroupBy(static seed => seed.Document),
            AnalysisParallelism.CreateOptions(CancellationToken.None),
            group =>
            {
                var semanticModel = group.Key.GetSemanticModelAsync().GetAwaiter().GetResult();
                if (semanticModel is null)
                {
                    return;
                }

                foreach (var (_, typeSyntax) in group)
                {
                    if (semanticModel.GetTypeInfo(typeSyntax).Type is INamedTypeSymbol namedType)
                    {
                        var resolvedType = normalizeToDefinition ? namedType.OriginalDefinition : namedType;
                        lock (sync)
                        {
                            resolved.Add(resolvedType);
                        }
                    }
                }
            });
    }

    private static bool ImplementsFrameworkContract(INamedTypeSymbol type)
    {
        foreach (var contract in type.AllInterfaces)
        {
            if (FrameworkDispatchedContracts.Contains(contract.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsFrameworkContractMember(ISymbol member)
    {
        if (member is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 } explicitMethod
            && explicitMethod.ExplicitInterfaceImplementations.Any(static impl => FrameworkDispatchedContracts.Contains(impl.ContainingType.Name)))
        {
            return true;
        }

        if (member is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } explicitProperty
            && explicitProperty.ExplicitInterfaceImplementations.Any(static impl => FrameworkDispatchedContracts.Contains(impl.ContainingType.Name)))
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
            if (!FrameworkDispatchedContracts.Contains(contract.Name))
            {
                continue;
            }

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
