using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Jellyfin.Plugin.AudioGateway.Services;

public sealed class CloudMigrationQueue
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly ConcurrentDictionary<string, byte> _pending =
        new(System.StringComparer.OrdinalIgnoreCase);

    public ChannelReader<string> Reader => _channel.Reader;

    public bool TryEnqueue(string jobId)
    {
        if (!_pending.TryAdd(jobId, 0))
        {
            return true;
        }

        if (_channel.Writer.TryWrite(jobId))
        {
            return true;
        }

        _pending.TryRemove(jobId, out _);
        return false;
    }

    public void Complete(string jobId)
        => _pending.TryRemove(jobId, out _);
}
