using System.Text.RegularExpressions;
using PortfolioApi.Common;
using PortfolioApi.Interfaces;

namespace PortfolioApi.Services.Ai.Steps;

/// <summary>
/// Step 4 — Burmese language quality and markdown hygiene.
/// <para>
/// Burmese is where a single-shot prompt degrades first: under a long structural
/// instruction the model drifts into English clauses ("Section", "House", "career") and
/// mixes markdown marks. This pass has one job and the whole reading in front of it, so
/// it can fix register and consistency without also juggling astrology.
/// </para>
/// <para>
/// It is skipped entirely for English readings and when the text is already clean — a
/// rewrite risks damaging good output, so it must earn its call.
/// </para>
/// </summary>
public sealed class LanguagePolishStep : IReadingStep
{
    public const string StepId = "polish";
    public string Id => StepId;

    private readonly IChatModel _model;
    private readonly ILogger<LanguagePolishStep> _log;

    public LanguagePolishStep(IChatModel model, ILogger<LanguagePolishStep> log)
    {
        _model = model;
        _log = log;
    }

    // A header line that also carries emphasis marks, i.e. "### **Title**" — the exact
    // malformation the old single prompt kept producing.
    private static readonly Regex MixedHeader = new(@"^\s*#{1,6}[^\n]*\*", RegexOptions.Compiled | RegexOptions.Multiline);

    // A run of 4+ Latin letters, which in a Burmese reading almost always means an
    // English word leaked in. Vedic terms are transliterated, so they never match. A few
    // stray matches are tolerated below rather than filtered here, since the cost of a
    // miscount is only whether the polish call happens.
    private static readonly Regex LatinRun = new(@"\b[A-Za-z]{4,}\b", RegexOptions.Compiled);

    private const string System =
"""
You are a Burmese language editor for a professional astrology practice. You edit prose
for language quality only. You never change the astrological content: not a placement,
not a house number, not a dasha, not a remedy, not a section order.

Your edits are limited to:
- Replacing English words and phrases with natural Burmese. Established Vedic terms stay
  in Burmese transliteration (ဒသာ, အန္တရ်ဒသာ, အဋ္ဌကဝဂ်, ဆဒ္ဗလ, လဂ်နာ).
- Fixing grammar, particles, and awkward or machine-translated phrasing so it reads as
  though written by an educated Burmese author.
- Repairing markdown so headers are "### " or "#### " lines containing ONLY hashes and
  heading text (never with "*"), and bullets are "- " lines.

Output the complete corrected document and nothing else.
""";

    public async Task<ApiResponse<string>> RunAsync(ReadingContext ctx, CancellationToken ct = default)
    {
        var text = ctx.Get(SynthesisStep.StepId);
        if (string.IsNullOrWhiteSpace(text))
            return ApiResponse<string>.Fail("Nothing to polish — the synthesis step produced no text.", 500);

        if (!ctx.Burmese) return ApiResponse<string>.Ok(text, "English reading — polish not required.");

        var latinWords = LatinRun.Matches(text).Count;
        var mixedHeaders = MixedHeader.Matches(text).Count;

        // A handful of Latin runs is normal (a year, an initial); a rewrite of clean text
        // is pure risk, so the pass only runs when there is a real defect to fix.
        if (latinWords <= 6 && mixedHeaders == 0)
            return ApiResponse<string>.Ok(text, "Already clean — polish skipped.");

        _log.LogInformation("Language polish: {Latin} English run(s), {Headers} malformed header(s).",
            latinWords, mixedHeaders);

        var result = await _model.CompleteAsync(System,
$"""
Edit the Burmese astrology reading below for language quality and markdown hygiene only.

Known defects to fix:
- {latinWords} run(s) of English words that should be natural Burmese.
- {mixedHeaders} header line(s) that mix "#" with "*".

=== DOCUMENT ===
{text}
""", new ChatOptions { Temperature = 0.2, MaxOutputTokens = 8192 }, ct);

        // Never accept a "polished" version that lost content — a truncated rewrite is a
        // much worse failure than slightly rough phrasing.
        if (!result.Success || (result.Data?.Length ?? 0) < text.Length * 0.6)
        {
            _log.LogWarning("Language polish rejected (returned {New} chars for {Old}); keeping the original.",
                result.Data?.Length ?? 0, text.Length);
            return ApiResponse<string>.Ok(text, "Polish rejected — original kept.");
        }

        return ApiResponse<string>.Ok(result.Data!.Trim(), "Polished.");
    }
}
