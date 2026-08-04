using PortfolioApi.Common;
using PortfolioApi.Services.Ai;

namespace PortfolioApi.Interfaces;

/// <summary>
/// One stage of the reading pipeline.
/// <para>
/// Splitting the reading into steps means a transient provider failure retries a
/// single stage instead of discarding a finished 7-area draft, and each stage gets a
/// prompt narrow enough to actually validate — the single-shot prompt asked for
/// grounding but nothing could check it.
/// </para>
/// </summary>
public interface IReadingStep
{
    /// <summary>Stable identifier — the key intermediate output is persisted under.</summary>
    string Id { get; }

    /// <summary>Produces this step's output from the chart and earlier steps' outputs.</summary>
    Task<ApiResponse<string>> RunAsync(ReadingContext ctx, CancellationToken ct = default);
}
