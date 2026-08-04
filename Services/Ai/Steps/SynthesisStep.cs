using PortfolioApi.Common;
using PortfolioApi.Interfaces;

namespace PortfolioApi.Services.Ai.Steps;

/// <summary>
/// Step 3 — write the surrounding document and bind the seven drafts into one voice.
/// <para>
/// The area drafts were written independently, so this stage supplies what none of them
/// could: the opening character portrait, the current-dasha reading, the remedies drawn
/// from the weaknesses across all areas, and a consistency pass over the whole.
/// </para>
/// </summary>
public sealed class SynthesisStep : IReadingStep
{
    public const string StepId = "synthesis";
    public string Id => StepId;

    private readonly IChatModel _model;

    public SynthesisStep(IChatModel model) => _model = model;

    private const string System =
"""
You are an expert professional Vedic astrologer writing under the pen name
'ဆရာဘုန်းမင်းသိုက်ဒင်' (Sayar Bhone Min Thike Din). You are assembling a finished reading
from drafted sections.

ABSOLUTE RULES
0. PEN NAME — Sign only as 'ဆရာဘုန်းမင်းသိုက်ဒင်'. NEVER use or reveal the real name
   'Myo Thant Naing' / 'မျိုးသန့်နိုင်'.
1. GROUNDING — Every claim names the specific chart factor behind it. Never introduce a
   placement, dasha, nakshatra or score that is not in the data.
2. CLEAN MARKDOWN — Use ONLY these marks, never combined:
     Section headers → a line starting with "### "
     Sub-headers     → a line starting with "#### "
     Bullets         → a line starting with "- "
     Emphasis        → **bold** around a few words INSIDE a sentence
   A header line contains ONLY hashes and heading text. Forbidden: "**## Header**",
   "## **Header**", or any header line containing "*".
3. HONESTY & CARE — Precise calculation, humble interpretation. Never induce fear. No
   definitive medical, legal or financial directives — offer gentle practical remedies
   (ဥပါယ်) and reflection.
""";

    public async Task<ApiResponse<string>> RunAsync(ReadingContext ctx, CancellationToken ct = default)
    {
        var mm = ctx.Burmese;

        var headings = mm
            ? new[]
            {
                "### ၁။ နိဒါန်းနှင့် ဇာတာရှင်၏ အခြေခံ သဘာဝ",
                "### ၂။ ဘဝကဏ္ဍ (၇) ရပ် အသေးစိတ် ဟောစာတမ်း",
                "### ၃။ လက်ရှိ ဖြတ်သန်းနေသော ဒသာကာလ သုံးသပ်ချက်",
                "### ၄။ ယတြာနှင့် အကြံပြုချက်",
            }
            : new[]
            {
                "### 1. Introduction and the native's essential nature",
                "### 2. The seven life areas in detail",
                "### 3. The current dasha period",
                "### 4. Remedies and guidance",
            };

        var language = mm
            ? "Write every word you add in 100% fluent, natural Burmese (မြန်မာ). No English "
            + "sentences. The only permitted foreign words are established Vedic terms in Burmese "
            + "transliteration (ဒသာ, အန္တရ်ဒသာ, အဋ္ဌကဝဂ်, ဆဒ္ဗလ, လဂ်နာ)."
            : "Write in clear, warm English.";

        var user =
$"""
{ctx.ChartFacts()}

=== TECHNICAL ANALYSIS ===
{ctx.Get(ChartAnalysisStep.StepId)}

=== DRAFTED LIFE-AREA SECTIONS (keep these, refine only for consistency and flow) ===
{ctx.Get(LifeAreaDraftStep.StepId)}

=== TASK ===
{language}

Assemble the complete reading with exactly these four sections, in this order and with
these exact headers:

{headings[0]}
Analyse the Lagna and the Moon sign: core personality, mind, and life theme. Substantial
prose, grounded in the specific placements.

{headings[1]}
Reproduce the seven drafted sub-sections above, keeping their "#### " headers and their
substance. Edit only to remove repetition between them, to fix any statement that
contradicts the chart or another section, and to make the voice continuous.

{headings[2]}
Explain what the current Mahadasha and Antardasha mean for the querent right now, and
connect it to the Sade Sati status if one is given.

{headings[3]}
Practical remedies (ဥပါယ်) and constructive advice, drawn from the weak or afflicted
factors identified in the analysis and echoed across the life areas.

Finish with ONE short, warm closing sentence, then a single humble line reminding the
querent that the computation is precise but the reading is guidance for reflection.

Output the finished reading only — no preamble, no commentary about your process.
""";

        var result = await _model.CompleteAsync(System, user, new ChatOptions { MaxOutputTokens = 8192 }, ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Data)) return result;

        var issues = ChartGrounding.Check(result.Data!, ctx.Chart);
        if (issues.Count == 0) return result;

        var retry = await _model.CompleteAsync(System,
$"""
{ctx.ChartFacts()}

=== THE READING YOU PRODUCED ===
{result.Data}

=== REQUIRED CORRECTION ===
{ChartGrounding.BuildCorrection(issues)}

Output the full corrected reading, complete and unabridged, with the same four sections
and the same headers. Change nothing except the contradicting statements.
""", new ChatOptions { Temperature = 0.15, MaxOutputTokens = 8192 }, ct);

        // Prefer a corrected reading, but never trade a complete one for a truncated retry.
        return retry.Success && (retry.Data?.Length ?? 0) > (result.Data!.Length / 2) ? retry : result;
    }
}
