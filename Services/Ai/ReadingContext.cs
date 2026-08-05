using System.Text;
using PortfolioApi.DTOs.Astrology;

namespace PortfolioApi.Services.Ai;

/// <summary>The seven life areas the reading covers, in presentation order.</summary>
public sealed record LifeArea(string Id, string TitleMm, string TitleEn, string TitleJa, string Focus);

/// <summary>
/// Everything a step needs: the computed chart, the language, and whatever earlier
/// steps produced. Steps read from <see cref="Outputs"/> by step id rather than
/// receiving each other directly, so the chain can be reordered or resumed midway.
/// </summary>
public sealed class ReadingContext
{
    public required AiReadingRequestDto Chart { get; init; }

    /// <summary>Reading request row id, when the run is resumable. Null for one-off runs.</summary>
    public int? RequestId { get; init; }

    /// <summary>Normalised reading language: "my" (Burmese, default), "en", or "ja".</summary>
    public string Lang
    {
        get
        {
            var raw = (Chart.Language ?? "my").Trim().ToLowerInvariant();
            return raw is "en" or "english" ? "en"
                 : raw is "ja" or "jp" or "japanese" ? "ja"
                 : "my";
        }
    }

    public bool Burmese => Lang == "my";
    public bool Japanese => Lang == "ja";
    public bool English => Lang == "en";

    /// <summary>The life-area heading in the reading's language.</summary>
    public string AreaTitle(LifeArea a) => Japanese ? a.TitleJa : Burmese ? a.TitleMm : a.TitleEn;

    /// <summary>
    /// The strict, per-language writing instruction injected into every client-facing
    /// drafting/synthesis step, so the whole reading is produced natively in the chosen
    /// language rather than translated after the fact.
    /// </summary>
    public string LanguageDirective() => Lang switch
    {
        "ja" =>
"""
You MUST write the ENTIRE response in Japanese. Use highly professional, polite, and empathetic
business-level Japanese — proper 敬語 (Keigo) and 丁寧語 (Teineigo) throughout — as a master Vedic
astrologer (インド占星術師) addressing a valued client. Do NOT write any sentence in English or
Burmese. Use correct Japanese astrological terminology: ダシャー (Dasha), アンタルダシャー
(Antardasha), ハウス (house), 惑星 (planet), 星座 (zodiac sign), ラグナ (Lagna / ascendant),
ナクシャトラ (Nakshatra), アシュタカヴァルガ (Ashtakavarga), シャドバラ (Shadbala). Format with
clear "### " headings and well-spaced "- " bullet points.
""",
        "en" =>
"""
Write the ENTIRE response in professional, premium English — the warm, polished register of an
expert astrologer writing for a discerning client. Keep established Sanskrit/Vedic terms
(Dasha, Lagna, Nakshatra) as-is. Use clear "### " headings and "- " bullet points.
""",
        _ =>
"""
Write every word in 100% fluent, natural, respectful Burmese (မြန်မာ). No English sentences. The
only permitted foreign words are established Vedic terms in Burmese transliteration (ဒသာ,
အန္တရ်ဒသာ, အဋ္ဌကဝဂ်, ဆဒ္ဗလ, လဂ်နာ). Use clear "### " headings and "- " bullet points.
""",
    };

    /// <summary>Completed step outputs, keyed by step id — including any restored from a
    /// previous partial run.</summary>
    public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);

    public string Get(string stepId) => Outputs.TryGetValue(stepId, out var v) ? v : string.Empty;

    /// <summary>The seven areas, with the houses/planets each one must stay anchored to.</summary>
    public static readonly IReadOnlyList<LifeArea> Areas = new List<LifeArea>
    {
        new("education", "ပညာရေးနှင့် ဉာဏ်ရည်", "Education & intellect", "教育と知性",
            "the 4th and 5th houses, Mercury, and Jupiter"),
        new("career", "အလုပ်အကိုင်နှင့် စီးပွားရေး", "Career & livelihood", "仕事と天職",
            "the 10th (karma), 2nd (dhana) and 11th (gains) houses and their lords"),
        new("wealth", "ငွေကြေးနှင့် ဓနဥစ္စာ", "Wealth & money", "財運と資産",
            "the 2nd and 11th houses plus the Sarvashtakavarga scores of the relevant signs"),
        new("marriage", "အချစ်ရေးနှင့် အိမ်ထောင်ရေး", "Love & marriage", "恋愛と結婚",
            "the 7th house, its lord, and Venus"),
        new("health", "ကျန်းမာရေး", "Health & vitality", "健康と活力",
            "the 6th house, the Ascendant lord, and the Sun"),
        new("society", "လူမှုဆက်ဆံရေးနှင့် ပတ်ဝန်းကျင်", "Community & relationships", "人間関係と社会",
            "the 3rd and 11th houses and Mars"),
        new("dharma", "ကံတရားနှင့် ဘာသာရေး", "Dharma & fortune", "天命と幸運",
            "the 9th house, its lord, and Jupiter"),
    };

    /// <summary>
    /// The chart rendered as a compact, clearly-labelled block. Every step is given the
    /// same rendering, so no step can reason about a placement a different step never saw.
    /// </summary>
    public string ChartFacts()
    {
        var r = Chart;
        var sb = new StringBuilder();
        sb.AppendLine("=== CHART SNAPSHOT (computed by the engine — the ONLY admissible facts) ===");

        if (!string.IsNullOrWhiteSpace(r.Name))
            sb.AppendLine($"Querent: {r.Name}" + (string.IsNullOrWhiteSpace(r.Gender) ? "" : $" ({r.Gender})"));
        if (!string.IsNullOrWhiteSpace(r.NayNan)) sb.AppendLine($"Myanmar birth-day sign (နေ့နံ): {r.NayNan}");
        if (!string.IsNullOrWhiteSpace(r.Ascendant)) sb.AppendLine($"Ascendant (Lagna): {r.Ascendant}");
        if (!string.IsNullOrWhiteSpace(r.MoonSign)) sb.AppendLine($"Moon sign (Chandra Rasi): {r.MoonSign}");
        if (!string.IsNullOrWhiteSpace(r.SunSign)) sb.AppendLine($"Sun sign: {r.SunSign}");

        if (r.Placements.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Planetary placements:");
            foreach (var p in r.Placements)
            {
                var bits = new List<string> { $"House {p.House}", p.Sign };
                if (!string.IsNullOrWhiteSpace(p.Nakshatra)) bits.Add($"Nak. {p.Nakshatra}");
                if (!string.IsNullOrWhiteSpace(p.Dignity)) bits.Add(p.Dignity!);
                if (p.Retrograde) bits.Add("retrograde");
                sb.AppendLine($"  - {p.Planet}: {string.Join(", ", bits)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Current Vimshottari dasha:");
        sb.AppendLine($"  - Mahadasha: {Or(r.Mahadasha)}");
        sb.AppendLine($"  - Antardasha: {Or(r.Antardasha)}");
        sb.AppendLine($"  - Pratyantardasha: {Or(r.Pratyantardasha)}");
        if (!string.IsNullOrWhiteSpace(r.DashaWindow)) sb.AppendLine($"  - Window: {r.DashaWindow}");

        if (!string.IsNullOrWhiteSpace(r.SadeSatiStatus))
            sb.AppendLine($"\nSade Sati: {r.SadeSatiStatus}");

        if (r.SarvashtakavargaBySign is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"Sarvashtakavarga per sign (Aries→Pisces): {string.Join(", ", r.SarvashtakavargaBySign)}");
            if (!string.IsNullOrWhiteSpace(r.AshtakavargaNotes)) sb.AppendLine($"Ashtakavarga notes: {r.AshtakavargaNotes}");
        }

        if (r.Yogas is { Count: > 0 })
            sb.AppendLine($"\nActive yogas: {string.Join(", ", r.Yogas)}");

        if (r.FocusAreas is { Count: > 0 })
            sb.AppendLine($"\nPlease emphasise these life areas: {string.Join(", ", r.FocusAreas)}");

        if (!string.IsNullOrWhiteSpace(r.ExtraContext))
            sb.AppendLine($"\nAdditional context:\n{r.ExtraContext}");

        return sb.ToString();
    }

    private static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "(unknown)" : s;
}
