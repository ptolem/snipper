namespace CoreLib;

// Stand-ins for the xUnit lifecycle contracts Snipper recognizes by name,
// so the fixture needs no real test-framework package (same pattern as
// FactAttribute in DeadCode.cs).

public interface IAsyncLifetime
{
    Task InitializeAsync();

    Task DisposeAsync();
}

// xUnit invokes fixture lifecycle methods reflectively through IAsyncLifetime.
public sealed class FakeFixture : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class CollectionDefinitionAttribute : Attribute
{
    public CollectionDefinitionAttribute(string name)
    {
    }
}

// Referenced only by the collection-name string in [Collection("sample")] on
// test classes — xUnit resolves it by convention, never by symbol reference.
[CollectionDefinition("sample")]
public sealed class SampleCollectionDefinition
{
}
