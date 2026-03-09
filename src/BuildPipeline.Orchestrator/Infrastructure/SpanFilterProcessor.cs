using System.Diagnostics;
using OpenTelemetry;

namespace BuildPipeline.Orchestrator.Infrastructure;

/// <summary>
/// Drops noisy Temporal spans (StartActivity, CompleteWorkflow) that add
/// visual clutter without meaningful timing information.
/// </summary>
public sealed class SpanFilterProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        if (data.DisplayName.StartsWith("StartActivity:", StringComparison.Ordinal) ||
            data.DisplayName.StartsWith("CompleteWorkflow:", StringComparison.Ordinal))
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
