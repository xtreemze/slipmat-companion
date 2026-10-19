using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Applies the configured retention policy to reproducible analysis artifacts.</summary>
public sealed class AnalysisArtifactRetentionTask : IScheduledTask
{
    private readonly AnalysisArtifactRetentionService _retention;
    private readonly ILogger<AnalysisArtifactRetentionTask> _logger;

    /// <summary>Creates the daily artifact retention task.</summary>
    public AnalysisArtifactRetentionTask(
        AnalysisArtifactRetentionService retention,
        ILogger<AnalysisArtifactRetentionTask> logger)
    {
        _retention = retention;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Slipmat analysis artifact retention";

    /// <inheritdoc />
    public string Key => "SlipmatAnalysisArtifactRetention";

    /// <inheritdoc />
    public string Description =>
        "Optionally removes idle or least-recently-used reproducible analysis artifacts; source stamps and media are preserved.";

    /// <inheritdoc />
    public string Category => "Slipmat";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(0);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var result = _retention.ApplyConfiguredPolicy(config, DateTimeOffset.UtcNow);
        _logger.LogInformation(
            "Analysis artifact retention completed: enabled={Enabled}, deletedSubjects={DeletedSubjects}, deletedBytes={DeletedBytes}, remainingBytes={RemainingBytes}, skippedActiveSubjects={SkippedActiveSubjects}, failedSubjects={FailedSubjects}",
            result.Enabled,
            result.DeletedSubjects,
            result.DeletedBytes,
            result.RemainingBytes,
            result.SkippedActiveSubjects,
            result.FailedSubjects);
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }
}
