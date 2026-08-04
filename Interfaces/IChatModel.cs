using PortfolioApi.Common;

namespace PortfolioApi.Interfaces;

/// <summary>Per-call generation knobs. Defaults suit structured, low-improvisation prose.</summary>
public sealed class ChatOptions
{
    public double Temperature { get; init; } = 0.3;
    public double TopP { get; init; } = 0.9;
    public int MaxOutputTokens { get; init; } = 8192;
}

/// <summary>
/// One text completion against the configured provider.
/// <para>
/// The reading pipeline needs to make many small, differently-prompted calls, so the
/// provider plumbing (key resolution, error mapping, response parsing) is separated
/// from the astrology prompting. Swapping providers — or pointing a step at a cheaper
/// model — becomes a registration change rather than a prompt rewrite.
/// </para>
/// </summary>
public interface IChatModel
{
    /// <summary>The model id used for calls, recorded on the generated reading.</summary>
    string ModelId { get; }

    Task<ApiResponse<string>> CompleteAsync(
        string systemPrompt, string userPrompt, ChatOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Streams a completion as text deltas, in order, as the provider produces them —
    /// so a caller can forward tokens to the client without waiting for the whole
    /// document. A provider that cannot stream (or fails to start) simply yields
    /// nothing, and the caller falls back to the stored/complete text.
    /// </summary>
    IAsyncEnumerable<string> StreamCompleteAsync(
        string systemPrompt, string userPrompt, ChatOptions? options = null, CancellationToken ct = default);

    /// <summary>Verifies key + model against the provider without generating anything.</summary>
    Task<ApiResponse<object>> CheckHealthAsync(CancellationToken ct = default);
}
