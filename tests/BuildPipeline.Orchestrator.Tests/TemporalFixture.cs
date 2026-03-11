using System.Threading.Tasks;
using Temporalio.Testing;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class TemporalFixture : IAsyncLifetime
{
    public WorkflowEnvironment Env { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Env = await WorkflowEnvironment.StartLocalAsync();
    }

    public async Task DisposeAsync()
    {
        await Env.DisposeAsync();
    }
}
