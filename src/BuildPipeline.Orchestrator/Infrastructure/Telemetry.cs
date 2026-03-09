using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BuildPipeline.Orchestrator.Infrastructure;

/// <summary>
/// Centralized OpenTelemetry instrumentation definitions.
/// One <see cref="ActivitySource"/> for custom tracing spans and one
/// <see cref="Meter"/> for custom metrics — both registered with the OTel SDK
/// via their names in Program.cs.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "build-pipeline";

    // --- Tracing -----------------------------------------------------------
    public static readonly ActivitySource Source = new(ServiceName);

    // --- Metrics ------------------------------------------------------------
    public static readonly Meter Meter = new(ServiceName);

    public static readonly Counter<long> BuildsTotal =
        Meter.CreateCounter<long>(
            "pipeline.builds.total",
            description: "Total number of platform builds executed");

    public static readonly Histogram<double> BuildDuration =
        Meter.CreateHistogram<double>(
            "pipeline.build.duration_ms",
            unit: "ms",
            description: "Duration of platform builds in milliseconds");

    public static readonly Counter<long> ValidationsTotal =
        Meter.CreateCounter<long>(
            "pipeline.validations.total",
            description: "Total number of project validations executed");
}
