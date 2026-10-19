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
        "Reconciles cached waveform, spectral and loudness analysis for local audio; cloud mounts are request-driven.";

    public string Category => "Library";

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var generated = 0;
        var skipped = 0;
        var unsupported = 0;
        var failed = 0;
        var cloudExcluded = 0;
        var completed = 0;

        try
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                MediaTypes = [MediaType.Audio],
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true,
            });

            var config = _configurationSource.GetCurrent();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100);
                return;
            }

            for (var index = 0; index < total; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (items[index] is Audio audio && audio.IsFileProtocol)
                {
                    if (RuntimeSettings.IsCloudProjectionPath(config, audio.Path))
                    {
                        cloudExcluded++;
                    }
                    else
                    {
                        var result = await _analyzer.AnalyzeItemAsync(
                            audio.Id, cancellationToken, onDemand: false).ConfigureAwait(false);
                        switch (result.Status)
                        {
                            case IntegratedAnalysisStatus.Generated:
                                generated++;
                                break;
                            case IntegratedAnalysisStatus.Skipped:
                                skipped++;
                                break;
                            case IntegratedAnalysisStatus.Unsupported:
                                unsupported++;
                                break;
                            case IntegratedAnalysisStatus.Failed:
                                failed++;
                                break;
                        }
                    }
                }
                else
                {
                    unsupported++;
                }

                completed++;
                progress.Report(completed * 100d / total);
            }
        }
        finally
        {
            elapsed.Stop();
            // Aggregate only; never log source paths, titles, or credentials.
            _logger.LogInformation(
                "Slipmat scheduled analysis {Outcome}: examined={Examined}, generated={Generated}, skipped={Skipped}, unsupported={Unsupported}, failed={Failed}, cloudExcluded={CloudExcluded}, elapsedMs={ElapsedMs}",
                cancellationToken.IsCancellationRequested ? "cancelled" : "ended",
                completed, generated, skipped, unsupported, failed, cloudExcluded,
                elapsed.ElapsedMilliseconds);
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
