using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

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

    public AnalysisBackfillTask(
        ILibraryManager library,
        IntegratedAudioAnalyzer analyzer,
        ICloudProjectionConfigurationSource configurationSource)
    {
        _library = library;
        _analyzer = analyzer;
        _configurationSource = configurationSource;
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
                await _analyzer.AnalyzeItemAsync(audio.Id, cancellationToken).ConfigureAwait(false);
            }

            progress.Report((index + 1) * 100d / total);
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
