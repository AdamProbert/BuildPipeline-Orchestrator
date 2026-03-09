using System.Diagnostics;
using OpenTelemetry;

namespace BuildPipeline.Orchestrator.Infrastructure;

/// <summary>
/// Drops the CompleteWorkflow span which is a zero-duration leaf
/// that adds no diagnostic value.
/// </summary>
public sealed class SpanFilterProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        if (data.DisplayName.StartsWith("CompleteWorkflow:", StringComparison.Ordinal))
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
