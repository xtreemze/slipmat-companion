using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Restores configured rclone VFS mounts once when the Jellyfin host starts.
/// This never queues a Jellyfin catalog scan or Companion analysis backfill.
/// </summary>
public sealed class CloudProjectionStartupReconciler : BackgroundService
{
    private readonly CloudProjectionService _projectionService;
    private readonly ILogger<CloudProjectionStartupReconciler> _logger;

    public CloudProjectionStartupReconciler(
        CloudProjectionService projectionService,
        ILogger<CloudProjectionStartupReconciler> logger)
    {
        _projectionService = projectionService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Jellyfin finish host startup before invoking external mount work.
        await Task.Yield();

        var projectionIds = _projectionService.GetProjectionIds();
        foreach (var projectionId in projectionIds)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                var result = await _projectionService
                    .ReconcileAsync(projectionId, stoppingToken, queueLibraryScan: false)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Cloud projection {ProjectionId} startup reconcile completed with status {Status} ({Code})",
                    projectionId,
                    result.Status,
                    result.Code);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Cloud projection {ProjectionId} startup reconcile failed; continuing with other profiles",
                    projectionId);
            }
        }
    }
}
