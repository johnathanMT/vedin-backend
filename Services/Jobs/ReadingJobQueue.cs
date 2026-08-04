using System.Threading.Channels;
using PortfolioApi.Interfaces;

namespace PortfolioApi.Services.Jobs;

/// <summary>
/// In-process bounded queue of reading-request ids awaiting AI generation.
/// <para>
/// A <see cref="Channel{T}"/> is deliberately sufficient at current volume: readings
/// are gated behind manual Sayar approval, so arrivals are human-paced. The queue is
/// bounded rather than unbounded so a provider outage produces visible back-pressure
/// instead of unbounded memory growth.
/// </para>
/// <para>
/// The trade-off is durability: a process restart loses queued ids. That is recovered
/// on startup by <see cref="ReadingJobWorker"/>, which re-enqueues any request still
/// sitting in the Queued/Processing state.
/// </para>
/// </summary>
public sealed class ReadingJobQueue : IReadingJobQueue
{
    private readonly Channel<int> _channel;
    private int _depth;

    public ReadingJobQueue()
    {
        _channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity: 200)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public int Depth => Volatile.Read(ref _depth);

    public bool TryEnqueue(int readingRequestId)
    {
        if (!_channel.Writer.TryWrite(readingRequestId)) return false;
        Interlocked.Increment(ref _depth);
        return true;
    }

    public async IAsyncEnumerable<int> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _depth);
            yield return id;
        }
    }
}
