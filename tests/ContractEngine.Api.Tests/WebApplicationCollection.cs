namespace ContractEngine.Api.Tests;

/// <summary>
/// xUnit collection marker for tests that boot <c>Program.cs</c> through
/// <c>WebApplicationFactory&lt;Program&gt;</c>. The shared static
/// <c>Serilog.Log.Logger</c> is set up per factory in a static ctor; running
/// those factories concurrently within the same assembly causes test-class
/// instances to race on that shared state (the sink-based logger installed
/// by <c>RequestLoggingTestFactory</c> sees no events when another factory's
/// Program boot swaps Log.Logger in between). Assigning every factory-using
/// test class to this single collection makes xUnit serialize them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class WebApplicationCollection
{
    public const string Name = "WebApplication";
}
