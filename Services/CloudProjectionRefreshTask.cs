using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Periodically reconciles the configured rclone cloud projection.
/// </summary>
public sealed class CloudProjectionRefreshTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly CloudProjectionService _projectionService;
    private readonly ILogger<CloudProjectionRefreshTask> _logger;

    public CloudProjectionRefreshTask(
        CloudProjectionService projectionService,
        ILogger<CloudProjectionRefreshTask> logger)
    {
        _projectionService = projectionService;
        _logger = logger;
    }

    public string Name => "Refresh Cloud Media Projection";

    public string Key => "SlipmatRcloneCloudProjectionRefresh";

    public string Description =>
        "Checks every configured bounded read-only rclone cloud mount and queues Jellyfin namespace scans without copying the media libraries or scheduling cloud analysis backfill.";

    public string Category => "Slipmat";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(0);

        var projectionIds = _projectionService.GetProjectionIds();
        if (projectionIds.Count == 0)
        {
            progress.Report(100);
            return;
        }

        for (var index = 0; index < projectionIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectionId = projectionIds[index];
            try
            {
                var result = await _projectionService
                    .ReconcileAsync(projectionId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Cloud projection {ProjectionId} reconcile completed with status {Status} ({Code})",
                    projectionId,
                    result.Status,
                    result.Code);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Cloud projection {ProjectionId} reconcile failed; continuing with other profiles",
                    projectionId);
            }

            progress.Report((index + 1) * 100d / projectionIds.Count);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks,
                MaxRuntimeTicks = TimeSpan.FromMinutes(5).Ticks,
            },
        ];
    }
}
