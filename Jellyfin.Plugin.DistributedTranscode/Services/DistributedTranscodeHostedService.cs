using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

public sealed class DistributedTranscodeHostedService : IHostedService, IDisposable
{
    private readonly ILogger<DistributedTranscodeHostedService> _logger;
    private readonly MeshNodeService _meshNodeService;
    private readonly DistributeTranscodeService _distributeTranscodeService;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _healthLoopCancellation;
    private Task? _healthLoopTask;

    public DistributedTranscodeHostedService(
        MeshNodeService meshNodeService,
        DistributeTranscodeService distributeTranscodeService,
        ILogger<DistributedTranscodeHostedService> logger)
    {
        _meshNodeService = meshNodeService;
        _distributeTranscodeService = distributeTranscodeService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _distributeTranscodeService.Initialize();
        await _meshNodeService.StartAsync(cancellationToken).ConfigureAwait(false);

        _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _healthLoopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _healthLoopTask = Task.Run(() => RunHealthLoopAsync(_healthLoopCancellation.Token), _healthLoopCancellation.Token);
        _logger.LogInformation("Distributed transcoding hosted service started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_healthLoopCancellation is not null)
        {
            await _healthLoopCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_healthLoopTask is not null)
        {
            await _healthLoopTask.ConfigureAwait(false);
        }
    }

    private async Task RunHealthLoopAsync(CancellationToken cancellationToken)
    {
        if (_timer is null)
        {
            return;
        }

        while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _meshNodeService.PerformHealthCheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Distributed transcoding health check failed.");
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _healthLoopCancellation?.Dispose();
    }
}
