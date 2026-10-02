namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Analysis;
using Xunit;

/// <summary>
/// <see cref="DocumentIdentifierIndex"/> replaced a solution-wide
/// <c>SymbolFinder</c> scan for symbols that cannot escape their declaring file
/// (locals, parameters). The risk that trade introduces is a false positive: the
/// textual name is present in the document but resolves to a different symbol.
/// These tests pin that discrimination, which the textual pre-filter alone
/// cannot provide.
/// </summary>
public sealed class DocumentIdentifierIndexShould
{
    [Theory]
    [InlineData("var target = 1; return target;", "target", true)]      // direct read
    [InlineData("var target = 1; _ = \"target\"; return 0;", "target", false)] // string literal is not a usage
    [InlineData("var target = 1; var other = new Holder(); return other.target;", "target", false)] // foreign member
    public void Detect_Reference_By_Binding_Not_By_Textual_Presence_For_HasReference(
        string body,
        string name,
        bool expected)
    {
        // Arrange
        var (semanticModel, root, symbol) = CompileLocal(body, name);

        var index = DocumentIdentifierIndex.Build(root);

        // Act
        var hasReference = index.HasReference(semanticModel, symbol, name);

        // Assert
        hasReference.Should().Be(expected);
    }

    [Fact]
    public void Return_False_For_The_Outer_Local_When_An_Inner_Local_Shadows_Its_Name_For_HasReference()
    {
        // Arrange: two locals named "value" in nested scopes; only the inner is read.
        const string source = """
            public sealed class Shadow
            {
                public int Run(int seed)
                {
                    var value = seed;
                    {
                        var value = seed * 2;
                        return value;
                    }
                }
            }
            """;

        var (semanticModel, root, outer) = CompileFirstLocalNamed(source, "value");
        var index = DocumentIdentifierIndex.Build(root);

        // Act
        var outerIsReferenced = index.HasReference(semanticModel, outer, "value");

        // Assert: the read binds to the inner local, so it is not a reference to the outer.
        outerIsReferenced.Should().BeFalse();
    }

    [Fact]
    public void Return_False_When_The_Name_Is_Absent_From_The_Document_For_HasReference()
    {
        // Arrange
        const string source = """
            public sealed class Absent
            {
                public int Run(int seed) => seed + 1;
            }
            """;

        var (semanticModel, root, parameter) = CompileParameter(source, "Run", "seed");
        var index = DocumentIdentifierIndex.Build(root);

        // Act
        var hasReference = index.HasReference(semanticModel, parameter, "notPresentAnywhere");

        // Assert
        hasReference.Should().BeFalse();
    }

    [Fact]
    public void Return_True_For_A_Parameter_Read_Only_In_A_Nested_Lambda_For_HasReference()
    {
        // Arrange
        const string source = """
            public sealed class Captured
            {
                public int Run(int seed)
                {
                    Func<int> read = () => seed * 2;
                    return read();
                }
            }
            """;

        var (semanticModel, root, parameter) = CompileParameter(source, "Run", "seed");
        var index = DocumentIdentifierIndex.Build(root);

        // Act
        var hasReference = index.HasReference(semanticModel, parameter, "seed");

        // Assert
        hasReference.Should().BeTrue();
    }

    private static (SemanticModel SemanticModel, SyntaxNode Root, ISymbol Symbol) CompileLocal(string body, string name)
    {
        var source = $$"""
            public sealed class Subject
            {
                public int Run(int seed)
                {
                    {{body}}
                }

                private sealed class Holder
                {
                    public int target { get; set; }
                }
            }
            """;

        var (semanticModel, root) = Compile(source);
        var declared = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Identifier.Text == name)
            .ToArray();

        declared.Should().NotBeEmpty("the test source must declare a local named {0}", name);

        return (semanticModel, root, semanticModel.GetDeclaredSymbol(declared[0])!);
    }

    private static (SemanticModel SemanticModel, SyntaxNode Root, ISymbol Symbol) CompileFirstLocalNamed(
        string source,
        string name)
    {
        var (semanticModel, root) = Compile(source);
        var declared = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Identifier.Text == name)
            .ToArray();

        declared.Should().NotBeEmpty("the test source must declare a local named {0}", name);

        return (semanticModel, root, semanticModel.GetDeclaredSymbol(declared[0])!);
    }

    private static (SemanticModel SemanticModel, SyntaxNode Root, ISymbol Symbol) CompileParameter(
        string source,
        string methodName,
        string parameterName)
    {
        var (semanticModel, root) = Compile(source);
        var parameter = semanticModel.GetDeclaredSymbol(GetMethod(root, methodName))!
            .Parameters
            .Single(candidate => candidate.Name == parameterName);

        return (semanticModel, root, parameter);
    }

    private static MethodDeclarationSyntax GetMethod(SyntaxNode root, string name)
    {
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == name);
    }

    private static (SemanticModel SemanticModel, SyntaxNode Root) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "SubjectAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return (compilation.GetSemanticModel(tree), tree.GetRoot());
    }
}
