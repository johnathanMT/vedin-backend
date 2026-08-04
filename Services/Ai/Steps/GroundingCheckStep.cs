using PortfolioApi.Common;
using PortfolioApi.Interfaces;

namespace PortfolioApi.Services.Ai.Steps;

/// <summary>
/// Step 5 (final gate) — factual grounding.
/// <para>
/// A Vedic reading's credibility collapses the moment it cites a placement the chart
/// does not have ("Saturn in the 7th" when Saturn is in the 4th). No earlier step can
/// enforce this, because none of them has BOTH the finished prose and the authoritative
/// placements side by side. This pass does: it is handed the completed reading and the
/// engine-computed <see cref="ReadingContext.ChartFacts"/> and asked, at temperature 0,
/// to correct ONLY statements that contradict the placements — preserving language,
/// structure, length and every non-contradicted sentence.
/// </para>
/// <para>
/// It is deliberately best-effort: if the model call fails, or returns a suspiciously
/// short document (a truncation or refusal), the ORIGINAL reading is kept. Grounding may
/// only ever improve trust — it must never reduce reliability or discard a good reading.
/// </para>
/// </summary>
public sealed class GroundingCheckStep : IReadingStep
{
    public const string StepId = "grounding-check";
    public string Id => StepId;

    private readonly IChatModel _model;
    private readonly ILogger<GroundingCheckStep> _log;

    public GroundingCheckStep(IChatModel model, ILogger<GroundingCheckStep> log)
    {
        _model = model;
        _log = log;
    }

    private const string System =
"""
You are a fact-checker for a professional Vedic astrology reading. You are given the
AUTHORITATIVE computed placements (the only admissible facts) and a finished reading.

Your ONLY task is to correct statements in the reading that CONTRADICT the placements —
for example a planet named in the wrong house or sign, a wrong dasha lord, a wrong
Ascendant, or a wrong dignity. When you fix a contradiction, change as few words as
possible so the sentence still reads naturally.

Hard rules:
- Do NOT add new predictions, interpretations, remedies, or sections.
- Do NOT remove or shorten content that is not contradicted.
- Do NOT change the language, tone, section order, or markdown structure.
- Established Vedic terms stay in their existing Burmese transliteration.
- If the reading contains NO contradictions, return it completely unchanged.

Output the full reading (corrected or unchanged) and nothing else.
""";

    public async Task<ApiResponse<string>> RunAsync(ReadingContext ctx, CancellationToken ct = default)
    {
        // Check the most-finished text available (polished → synthesis).
        var text = ctx.Get(LanguagePolishStep.StepId);
        if (string.IsNullOrWhiteSpace(text)) text = ctx.Get(SynthesisStep.StepId);
        if (string.IsNullOrWhiteSpace(text))
            return ApiResponse<string>.Fail("Nothing to ground — no reading text was produced.", 500);

        var user =
$"""
{ctx.ChartFacts()}

=== READING TO VERIFY ===
{text}
""";

        var result = await _model.CompleteAsync(System, user, new ChatOptions { Temperature = 0.0, MaxOutputTokens = 8192 }, ct);

        // Best-effort: any doubt → keep the original. A truncated "correction" is a far
        // worse outcome than an ungrounded phrase slipping through.
        if (!result.Success || (result.Data?.Length ?? 0) < text.Length * 0.6)
        {
            _log.LogWarning("Grounding check not applied ({New} chars for {Old}); keeping the original reading. {Reason}",
                result.Data?.Length ?? 0, text.Length, result.Message);
            return ApiResponse<string>.Ok(text, "Grounding skipped — original kept.");
        }

        var corrected = result.Data!.Trim();
        if (string.Equals(corrected, text.Trim(), StringComparison.Ordinal))
            _log.LogInformation("Grounding check: no contradictions found.");
        else
            _log.LogInformation("Grounding check: corrected contradiction(s) against the computed chart.");

        return ApiResponse<string>.Ok(corrected, "Grounded against the computed chart.");
    }
}
