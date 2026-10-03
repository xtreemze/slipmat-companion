using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Single-worker bounded analysis queue. Jellyfin library callbacks only enqueue
/// identity and return; decoding never executes on the host event thread.
/// </summary>
public sealed class IntegratedAnalysisWorker : BackgroundService
{
    private const int QueueCapacity = 128;

    private readonly ILibraryManager _library;
    private readonly IntegratedAudioAnalyzer _analyzer;
    private readonly ICloudProjectionConfigurationSource _configurationSource;
    private readonly ILogger<IntegratedAnalysisWorker> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly ConcurrentDictionary<Guid, byte> _pending = [];
    private readonly ConcurrentDictionary<Guid, byte> _dirtyAgain = [];

    public IntegratedAnalysisWorker(
        ILibraryManager library,
        IntegratedAudioAnalyzer analyzer,
        ICloudProjectionConfigurationSource configurationSource,
        ILogger<IntegratedAnalysisWorker> logger)
    {
        _library = library;
        _analyzer = analyzer;
        _configurationSource = configurationSource;
        _logger = logger;
    }

    public bool TryEnqueue(Guid itemId)
    {
        if (_pending.TryAdd(itemId, 0))
        {
            if (_queue.Writer.TryWrite(itemId))
            {
                return true;
            }

            _pending.TryRemove(itemId, out _);
            return false;
        }

        _dirtyAgain[itemId] = 0;
        return true;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnLibraryItemChanged;
        _library.ItemUpdated += OnLibraryItemChanged;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnLibraryItemChanged;
        _library.ItemUpdated -= OnLibraryItemChanged;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var itemId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _analyzer.AnalyzeItemAsync(itemId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Queued analysis failed for Jellyfin item {ItemId}", itemId);
            }
            finally
            {
                _pending.TryRemove(itemId, out _);
                if (_dirtyAgain.TryRemove(itemId, out _))
                {
                    TryEnqueue(itemId);
                }
            }
        }
    }

    private void OnLibraryItemChanged(object? sender, ItemChangeEventArgs args)
    {
        if (args.Item is Audio audio
            && audio.IsFileProtocol
            && !RuntimeSettings.IsCloudProjectionPath(
                _configurationSource.GetCurrent(),
                audio.Path))
        {
            TryEnqueue(audio.Id);
        }
    }
}
