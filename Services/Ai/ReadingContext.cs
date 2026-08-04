using System.Text;
using PortfolioApi.DTOs.Astrology;

namespace PortfolioApi.Services.Ai;

/// <summary>The seven life areas the reading covers, in presentation order.</summary>
public sealed record LifeArea(string Id, string TitleMm, string TitleEn, string Focus);

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

    public bool Burmese => !string.Equals(Chart.Language, "en", StringComparison.OrdinalIgnoreCase);

    /// <summary>Completed step outputs, keyed by step id — including any restored from a
    /// previous partial run.</summary>
    public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);

    public string Get(string stepId) => Outputs.TryGetValue(stepId, out var v) ? v : string.Empty;

    /// <summary>The seven areas, with the houses/planets each one must stay anchored to.</summary>
    public static readonly IReadOnlyList<LifeArea> Areas = new List<LifeArea>
    {
        new("education", "ပညာရေးနှင့် ဉာဏ်ရည်", "Education & intellect",
            "the 4th and 5th houses, Mercury, and Jupiter"),
        new("career", "အလုပ်အကိုင်နှင့် စီးပွားရေး", "Career & livelihood",
            "the 10th (karma), 2nd (dhana) and 11th (gains) houses and their lords"),
        new("wealth", "ငွေကြေးနှင့် ဓနဥစ္စာ", "Wealth & money",
            "the 2nd and 11th houses plus the Sarvashtakavarga scores of the relevant signs"),
        new("marriage", "အချစ်ရေးနှင့် အိမ်ထောင်ရေး", "Love & marriage",
            "the 7th house, its lord, and Venus"),
        new("health", "ကျန်းမာရေး", "Health & vitality",
            "the 6th house, the Ascendant lord, and the Sun"),
        new("society", "လူမှုဆက်ဆံရေးနှင့် ပတ်ဝန်းကျင်", "Community & relationships",
            "the 3rd and 11th houses and Mars"),
        new("dharma", "ကံတရားနှင့် ဘာသာရေး", "Dharma & fortune",
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
