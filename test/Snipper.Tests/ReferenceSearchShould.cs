namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Xunit;

/// <summary>
/// <see cref="ReferenceSearch"/> decides from the symbol alone which documents to search,
/// so its answer is the same whichever rule asks first. The scope is pinned here on a
/// two-project solution - Lib, and App referencing it - where App spells every name Lib
/// declares, so a scope that is too wide shows up as an App document in the candidate set
/// and one that is too narrow as a missing App reference.
/// </summary>
public sealed class ReferenceSearchShould
{
    private const string LibSource = """
        namespace Lib;

        public interface IGreeter { void Greet(); }

        internal sealed class Hidden { internal void Run() { } }

        internal sealed class Greeter : IGreeter { void IGreeter.Greet() { } }

        public interface IFetcher { void Fetch(); }

        internal abstract class FetcherBase { public void Fetch() { } }

        internal sealed class Fetcher : FetcherBase, IFetcher { }

        public class Visible
        {
            private int _count;

            public void Show() { }

            public int this[int index] => index;
        }

        public sealed class MarkerAttribute : System.Attribute { }

        [Marker]
        public class Marked { }

        public static class Uses
        {
            public static void Go(Hidden hidden) => hidden.Run();
        }
        """;

    private const string AppSource = """
        namespace App;

        public static class Program
        {
            public static void Main()
            {
                new Lib.Visible().Show();
                _ = new Lib.Visible()[1];
                Lib.IGreeter greeter = null!;
                greeter.Greet();
                Lib.IFetcher fetcher = null!;
                fetcher.Fetch();
                Run();
            }

            private static void Run() { }

            private static int _count;
        }
        """;

    [Fact]
    public async Task Search_Only_The_Declaring_Project_For_A_Member_Hidden_Inside_It()
    {
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);

        var documents = search.GetCandidateDocuments(Member(lib, "Lib.Hidden", "Run"));

        documents.Select(static d => d.Project.Name).Should().OnlyContain(name => name == "Lib");
        (await search.HasAnyAsync(Member(lib, "Lib.Hidden", "Run"), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Search_The_Whole_Solution_For_A_Hidden_Member_When_The_Assembly_Has_Friends()
    {
        var (solution, lib) = await BuildAsync(friends: true);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);

        var documents = search.GetCandidateDocuments(Member(lib, "Lib.Hidden", "Run"));

        documents.Select(static d => d.Project.Name).Should().Contain("App");
    }

    [Fact]
    public async Task Search_Only_The_Declaring_Project_For_A_Private_Member_Even_When_The_Assembly_Has_Friends()
    {
        var (solution, lib) = await BuildAsync(friends: true);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);

        var references = await search.FindAsync(Member(lib, "Lib.Visible", "_count"), CancellationToken.None);

        references.Should().BeEmpty("App's _count is a different field, and only Lib can see this one");
    }

    [Fact]
    public async Task Find_References_Through_The_Interface_To_A_Private_Explicit_Implementation()
    {
        // An explicit implementation is private, but the search cascades to IGreeter.Greet,
        // which App calls. Confining it to Lib because it is private would lose that call.
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);

        var explicitGreet = lib.GetTypeByMetadataName("Lib.Greeter")!.GetMembers().OfType<IMethodSymbol>()
            .Single(static m => m.ExplicitInterfaceImplementations.Length > 0);

        var references = await search.FindAsync(explicitGreet, CancellationToken.None);

        references.Should().Contain(static r => r.Document.Project.Name == "App");
    }

    [Fact]
    public async Task Find_References_Through_An_Interface_A_Derived_Type_Implements_With_An_Inherited_Member()
    {
        // FetcherBase implements nothing, but Fetcher implements IFetcher with the Fetch it
        // inherits, so the search for FetcherBase.Fetch cascades to IFetcher.Fetch, which App
        // calls. FetcherBase is internal; the member being public is what keeps it unconfined.
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);

        var references = await search.FindAsync(Member(lib, "Lib.FetcherBase", "Fetch"), CancellationToken.None);

        references.Should().Contain(static r => r.Document.Project.Name == "App");
    }

    [Fact]
    public async Task Find_An_Attribute_Applied_By_Its_Short_Name()
    {
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);

        var found = await search.HasAnyAsync(lib.GetTypeByMetadataName("Lib.MarkerAttribute")!, CancellationToken.None);

        found.Should().BeTrue();
    }

    [Fact]
    public async Task Find_An_Indexer_In_Another_Project()
    {
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);
        var indexer = lib.GetTypeByMetadataName("Lib.Visible")!.GetMembers().OfType<IPropertySymbol>().Single(static p => p.IsIndexer);

        var references = await search.FindAsync(indexer, CancellationToken.None);

        references.Should().Contain(static r => r.Document.Project.Name == "App");
    }

    [Fact]
    public async Task Search_Each_Symbol_Once_However_Many_Callers_Ask()
    {
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);
        var show = Member(lib, "Lib.Visible", "Show");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => search.FindAsync(show, CancellationToken.None)));
        var later = await search.FindAsync(Member(lib, "Lib.Visible", "Show"), CancellationToken.None);

        // ImmutableArray equality is identity of the array underneath: one search, one result.
        results.Append(later).Distinct().Should().ContainSingle();
        later.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Search_Again_After_A_Cancelled_Search()
    {
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);
        var show = Member(lib, "Lib.Visible", "Show");

        var cancelled = () => search.FindAsync(show, new CancellationToken(canceled: true));
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        (await search.FindAsync(show, CancellationToken.None)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Answer_Any_Reference_The_Same_Way_The_Full_Search_Does()
    {
        // HasAnyAsync runs on a solution of its own, so it takes the stopping path rather
        // than reusing a full search; FindAsync on a second solution is the yardstick.
        var (anySolution, anyLib) = await BuildAsync(friends: false);
        var (allSolution, allLib) = await BuildAsync(friends: false);
        var anySearch = await ReferenceSearch.GetAsync(anySolution, CancellationToken.None);
        var allSearch = await ReferenceSearch.GetAsync(allSolution, CancellationToken.None);
        var pick = new Func<Compilation, ISymbol>[]
        {
            static c => Member(c, "Lib.Visible", "Show"),
            static c => Member(c, "Lib.Visible", "_count"),
            static c => Member(c, "Lib.Hidden", "Run"),
            static c => Member(c, "Lib.FetcherBase", "Fetch"),
            static c => c.GetTypeByMetadataName("Lib.Marked")!,
            static c => c.GetTypeByMetadataName("Lib.Uses")!,
        };

        foreach (var symbolIn in pick)
        {
            var any = await anySearch.HasAnyAsync(symbolIn(anyLib), CancellationToken.None);
            var all = await allSearch.FindAsync(symbolIn(allLib), CancellationToken.None);

            any.Should().Be(!all.IsEmpty, "HasAnyAsync must agree with FindAsync for {0}", symbolIn(anyLib).Name);
        }
    }

    [Fact]
    public async Task Answer_Any_Reference_Again_After_A_Cancelled_Search()
    {
        var (solution, lib) = await BuildAsync(friends: false);
        var search = await ReferenceSearch.GetAsync(solution, CancellationToken.None);
        var show = Member(lib, "Lib.Visible", "Show");

        var cancelled = () => search.HasAnyAsync(show, new CancellationToken(canceled: true));
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        (await search.HasAnyAsync(show, CancellationToken.None)).Should().BeTrue();
    }

    private static ISymbol Member(Compilation compilation, string typeName, string memberName)
    {
        return compilation.GetTypeByMetadataName(typeName)!.GetMembers(memberName).Single();
    }

    private static async Task<(Solution Solution, Compilation Lib)> BuildAsync(bool friends)
    {
        using var workspace = new AdhocWorkspace();
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var libId = ProjectId.CreateNewId("Lib");
        var appId = ProjectId.CreateNewId("App");
        var libSource = friends
            ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"App\")]\n" + LibSource
            : LibSource;

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(libId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.CSharp)
                .WithMetadataReferences([corlib]))
            .AddProject(ProjectInfo.Create(appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp)
                .WithMetadataReferences([corlib]))
            .AddProjectReference(appId, new ProjectReference(libId))
            .AddDocument(DocumentId.CreateNewId(libId), "Lib.cs", SourceText.From(libSource))
            .AddDocument(DocumentId.CreateNewId(appId), "App.cs", SourceText.From(AppSource));

        var lib = await solution.GetProject(libId)!.GetCompilationAsync();
        return (solution, lib!);
    }
}
