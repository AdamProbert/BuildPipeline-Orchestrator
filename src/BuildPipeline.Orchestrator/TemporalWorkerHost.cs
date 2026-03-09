using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Worker;

namespace BuildPipeline.Orchestrator;

public sealed class TemporalWorkerHost : IHostedService
{
    private readonly PipelineConfig _config;
    private readonly ILogger<TemporalWorkerHost> _logger;
    private readonly IPipelineActivities _activities;
    private CancellationTokenSource? _cts;
    private TemporalClient? _client;
    private TemporalWorker? _worker;
    private Task? _workerTask;

    public TemporalWorkerHost(PipelineConfig config, ILogger<TemporalWorkerHost> logger, IPipelineActivities activities)
    {
        _config = config;
        _logger = logger;
        _activities = activities;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Connecting Temporal worker to {Address}/{Namespace} on task queue {TaskQueue}",
            _config.TemporalAddress,
            _config.TemporalNamespace,
            _config.TaskQueue);

        try
        {
            _client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
            {
                TargetHost = _config.TemporalAddress,
                Namespace = _config.TemporalNamespace,
                Interceptors = [new TracingInterceptor()],
            });

            var options = new TemporalWorkerOptions(_config.TaskQueue)
                .AddWorkflow<PipelineWorkflow>()
                .AddAllActivities(_activities);

            _worker = new TemporalWorker(_client, options);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _workerTask = _worker.ExecuteAsync(_cts.Token);

            _logger.LogInformation("Temporal worker started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Temporal worker");
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Temporal worker");

        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_workerTask != null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during graceful shutdown
            }

            _workerTask = null;
        }

        _worker = null;
        _client = null;
    }
}
