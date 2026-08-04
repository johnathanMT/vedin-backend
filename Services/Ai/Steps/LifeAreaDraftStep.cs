using System.Collections.Concurrent;
using System.Text;
using PortfolioApi.Common;
using PortfolioApi.Interfaces;

namespace PortfolioApi.Services.Ai.Steps;

/// <summary>
/// Step 2 — draft the seven life areas, each as its own model call.
/// <para>
/// The calls run concurrently because they are independent given the analysis notes,
/// and each one is small enough that its output can be grounding-checked against the
/// chart on the spot — with one narrow retry naming the exact contradiction. A single
/// 8k-token call could do none of that: one bad sentence meant regenerating everything.
/// </para>
/// </summary>
public sealed class LifeAreaDraftStep : IReadingStep
{
    public const string StepId = "areas";
    public string Id => StepId;

    /// <summary>Gemini rate-limits aggressively on the free tier; three at a time is a
    /// safe balance between latency and 429s.</summary>
    private const int MaxParallel = 3;

    private readonly IChatModel _model;
    private readonly ILogger<LifeAreaDraftStep> _log;

    public LifeAreaDraftStep(IChatModel model, ILogger<LifeAreaDraftStep> log)
    {
        _model = model;
        _log = log;
    }

    private const string System =
"""
You are an expert professional Vedic astrologer writing under the pen name
'ဆရာဘုန်းမင်းသိုက်ဒင်' (Sayar Bhone Min Thike Din). You are drafting ONE section of a
longer reading. Another pass will assemble and polish the whole document, so do not
write an introduction, a conclusion, or a greeting.

ABSOLUTE RULES
1. GROUNDING — Every claim must name the specific factor it rests on (planet, house,
   dignity, nakshatra, dasha lord or Ashtakavarga score) exactly as given in the chart.
   Never state a placement that is not in the data. No generic Barnum statements that
   would be true of anyone.
2. HONESTY — Describe difficulty plainly but without inducing fear. No medical, legal
   or financial directives. Interpretations are guidance for reflection.
3. FORMAT — Output ONLY the section body as prose paragraphs, plus at most three
   "- " bullets if a list genuinely helps. No headers, no "###", no title line.
   Use **bold** for at most a few words inside a sentence.
4. PEN NAME — Never mention the real name 'Myo Thant Naing' / 'မျိုးသန့်နိုင်'.
""";

    public async Task<ApiResponse<string>> RunAsync(ReadingContext ctx, CancellationToken ct = default)
    {
        var analysis = ctx.Get(ChartAnalysisStep.StepId);
        var drafts = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var failures = new ConcurrentBag<string>();

        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = ReadingContext.Areas.Select(async area =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await DraftAreaAsync(ctx, area, analysis, ct);
                if (result.Success && !string.IsNullOrWhiteSpace(result.Data))
                    drafts[area.Id] = result.Data!;
                else
                    failures.Add($"{area.TitleEn}: {result.Message}");
            }
            finally { gate.Release(); }
        });

        await Task.WhenAll(tasks);

        // A partial reading is worse than no reading — the querent paid for all seven areas.
        if (!failures.IsEmpty)
            return ApiResponse<string>.Fail($"Could not draft every life area ({string.Join("; ", failures.Take(3))}).", 502);

        var sb = new StringBuilder();
        foreach (var area in ReadingContext.Areas)
        {
            sb.AppendLine($"#### {(ctx.Burmese ? area.TitleMm : area.TitleEn)}");
            sb.AppendLine();
            sb.AppendLine(drafts[area.Id].Trim());
            sb.AppendLine();
        }

        return ApiResponse<string>.Ok(sb.ToString().TrimEnd(), "Drafted.");
    }

    private async Task<ApiResponse<string>> DraftAreaAsync(
        ReadingContext ctx, LifeArea area, string analysis, CancellationToken ct)
    {
        var language = ctx.Burmese
            ? "Write in 100% fluent, natural Burmese (မြန်မာ). No English sentences. The only "
            + "permitted foreign words are established Vedic terms in Burmese transliteration "
            + "(ဒသာ, အန္တရ်ဒသာ, အဋ္ဌကဝဂ်, ဆဒ္ဗလ, လဂ်နာ)."
            : "Write in clear, warm English.";

        var user =
$"""
{ctx.ChartFacts()}

=== TECHNICAL ANALYSIS (from the previous stage — treat as authoritative) ===
{analysis}

=== TASK ===
Draft the life-area section: {area.TitleEn} ({area.TitleMm}).
Anchor it in {area.Focus}, and connect it to the current dasha where the analysis supports it.

{language}

Write 2-3 substantial paragraphs. Every paragraph must name at least one concrete factor
from the chart above. Output the section body only — no heading, no sign-off.
""";

        var first = await _model.CompleteAsync(System, user, new ChatOptions { MaxOutputTokens = 2048 }, ct);
        if (!first.Success || string.IsNullOrWhiteSpace(first.Data)) return first;

        var issues = ChartGrounding.Check(first.Data!, ctx.Chart);
        if (issues.Count == 0) return first;

        // One corrective pass. If it still contradicts the chart, the synthesis step's own
        // check will catch it — looping here would spend tokens for diminishing returns.
        _log.LogWarning("Grounding: {Count} contradiction(s) in area {Area}; requesting a correction.",
            issues.Count, area.Id);

        var retry = await _model.CompleteAsync(System,
$"""
{user}

=== YOUR PREVIOUS DRAFT ===
{first.Data}

=== REQUIRED CORRECTION ===
{ChartGrounding.BuildCorrection(issues)}

Output the corrected section body only.
""", new ChatOptions { Temperature = 0.15, MaxOutputTokens = 2048 }, ct);

        return retry.Success && !string.IsNullOrWhiteSpace(retry.Data) ? retry : first;
    }
}
