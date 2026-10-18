using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Dashboard-visible idempotent backfill/reconciliation task for integrated
/// analysis. Work remains serialized through <see cref="IntegratedAudioAnalyzer"/>.
/// </summary>
public sealed class AnalysisBackfillTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly IntegratedAudioAnalyzer _analyzer;
    private readonly ICloudProjectionConfigurationSource _configurationSource;
    private readonly ILogger<AnalysisBackfillTask> _logger;

    public AnalysisBackfillTask(
        ILibraryManager library,
        IntegratedAudioAnalyzer analyzer,
        ICloudProjectionConfigurationSource configurationSource,
        ILogger<AnalysisBackfillTask> logger)
    {
        _library = library;
        _analyzer = analyzer;
        _configurationSource = configurationSource;
        _logger = logger;
    }

    public string Name => "Slipmat audio analysis";

    public string Key => "SlipmatAudioGatewayAnalysis";

    public string Description =>
        "Precomputes optional Slipmat waveform artifacts for local Jellyfin audio; cloud mounts are request-driven.";

    public string Category => "Library";

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Audio],
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
        });

        var config = _configurationSource.GetCurrent();
        var total = items.Count;
        var attempted = 0;
        var excluded = 0;
        var completed = 0;
        var failed = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (total == 0)
            {
                progress.Report(100);
                return;
            }

            for (var index = 0; index < total; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (items[index] is Audio audio
                    && audio.IsFileProtocol
                    && !RuntimeSettings.IsCloudProjectionPath(config, audio.Path))
                {
                    attempted++;
                    try
                    {
                        await _analyzer.AnalyzeItemAsync(audio.Id, cancellationToken).ConfigureAwait(false);
                        completed++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Scheduled audio analysis failed for item {ItemId}", audio.Id);
                    }
                }
                else
                {
                    excluded++;
                }

                progress.Report((index + 1) * 100d / total);
            }
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation(
                "Slipmat audio analysis reconciliation: enumerated={Enumerated}, attempted={Attempted}, completed={Completed}, failed={Failed}, excluded={Excluded}, elapsedMs={ElapsedMs}, cancelled={Cancelled}",
                total, attempted, completed, failed, excluded,
                stopwatch.ElapsedMilliseconds, cancellationToken.IsCancellationRequested);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }
}
