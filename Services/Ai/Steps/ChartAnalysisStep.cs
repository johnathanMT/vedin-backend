using PortfolioApi.Common;
using PortfolioApi.Interfaces;

namespace PortfolioApi.Services.Ai.Steps;

/// <summary>
/// Step 1 — read the chart before writing about it.
/// <para>
/// Produces a terse technical analysis in English: strongest and weakest houses,
/// dignities, yogas that actually apply, what the current dasha activates. Nothing
/// here reaches the querent. Its purpose is to give the drafting steps one consistent
/// interpretation to work from, so the seven areas cannot contradict each other about
/// whether, say, an afflicted Saturn is a strength or a weakness.
/// </para>
/// </summary>
public sealed class ChartAnalysisStep : IReadingStep
{
    public const string StepId = "analysis";
    public string Id => StepId;

    private readonly IChatModel _model;

    public ChartAnalysisStep(IChatModel model) => _model = model;

    private const string System =
"""
You are a senior Vedic (Parashari) astrologer performing technical chart analysis for
another astrologer — not for the client. Write in compact English notes, not prose.

Rules:
- Use ONLY the chart data given. Never introduce a placement, dignity, dasha, nakshatra
  or score that is not listed. If something is not in the data, say "not given".
- Be specific and quantitative where the data allows (house numbers, dignities,
  Sarvashtakavarga values).
- Note contradictions honestly (e.g. a strong house lord in a weak sign) instead of
  smoothing them over. Later stages depend on this being accurate, not flattering.
""";

    public async Task<ApiResponse<string>> RunAsync(ReadingContext ctx, CancellationToken ct = default)
    {
        var user =
$"""
{ctx.ChartFacts()}

=== TASK ===
Produce structured analysis notes under exactly these headings:

[LAGNA] Ascendant sign and its lord: placement, dignity, what it means for the native's
constitution and life direction.
[MOON] Moon sign, nakshatra and condition: the emotional and mental register.
[STRENGTHS] The 3-5 genuinely strong factors, each with the specific evidence.
[WEAKNESSES] The 3-5 genuinely afflicted factors, each with the specific evidence.
[YOGAS] For each listed yoga: whether the placements given actually support it, and its effect.
[DASHA] What the current Mahadasha/Antardasha lords rule in this chart (houses owned,
placement, dignity), and therefore what this period activates.
[HOUSE MAP] One line per house 1-12: "H<n>: <occupants or empty> — <one-clause verdict>".

Keep the whole thing under 700 words. No client-facing language, no reassurance.
""";

        return await _model.CompleteAsync(System, user, new ChatOptions
        {
            Temperature = 0.2,          // analysis should be reproducible, not creative
            MaxOutputTokens = 2048,
        }, ct);
    }
}
