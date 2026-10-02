using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Periodically reconciles the operator-managed Jottacloud projection.
/// The service itself fails closed when the projection is disabled or unhealthy.
/// </summary>
public sealed class JottacloudProjectionRefreshTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly JottacloudProjectionService _projectionService;
    private readonly ILogger<JottacloudProjectionRefreshTask> _logger;

    public JottacloudProjectionRefreshTask(
        JottacloudProjectionService projectionService,
        ILogger<JottacloudProjectionRefreshTask> logger)
    {
        _projectionService = projectionService;
        _logger = logger;
    }

    public string Name => "Refresh Jottacloud Media Projection";

    public string Key => "SlipmatJottacloudProjectionRefresh";

    public string Description =>
        "Reconciles an operator-authenticated Jottacloud folder into the local Slipmat/Jellyfin projection.";

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
            "Jottacloud projection reconcile completed with status {Status} ({Code})",
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
                MaxRuntimeTicks = TimeSpan.FromMinutes(10).Ticks,
            },
        ];
    }
}
