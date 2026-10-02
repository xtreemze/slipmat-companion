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
        "Copies the configured rclone remote folder into the local Jellyfin cloud-media projection.";

    public string Category => "Slipmat";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(0);

        var result = await _projectionService
            .ReconcileAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Cloud projection reconcile completed with status {Status} ({Code})",
            result.Status,
            result.Code);

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks,
                MaxRuntimeTicks = TimeSpan.FromHours(12).Ticks,
            },
        ];
    }
}
