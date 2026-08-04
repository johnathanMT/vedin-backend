namespace PortfolioApi.Interfaces;

/// <summary>
/// Hands an approved reading request off to the background worker.
/// <para>
/// Approval used to call Gemini inline on a 60s HttpClient timeout, so the Sayar's
/// browser held an open request for the whole generation and any transient provider
/// failure surfaced as a failed approval. Enqueuing decouples the two: approval is
/// a database write, generation happens after the response is sent.
/// </para>
/// </summary>
public interface IReadingJobQueue
{
    /// <summary>Queues a reading for generation. Returns false when the queue is
    /// saturated, so the caller can fall back or report back-pressure.</summary>
    bool TryEnqueue(int readingRequestId);

    /// <summary>Consumed by the background worker; completes as jobs arrive.</summary>
    IAsyncEnumerable<int> DequeueAllAsync(CancellationToken ct);

    /// <summary>Approximate number of jobs waiting — surfaced on the admin screen.</summary>
    int Depth { get; }
}
