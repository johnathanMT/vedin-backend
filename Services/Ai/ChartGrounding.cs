using System.Text.RegularExpressions;
using PortfolioApi.DTOs.Astrology;

namespace PortfolioApi.Services.Ai;

/// <summary>A grounding violation: something the model asserted that the chart does not support.</summary>
public sealed record GroundingIssue(string Claim, string Reason);

/// <summary>
/// Checks generated prose against the computed chart.
/// <para>
/// Rule 3 of the old prompt told the model to name the factor behind every statement
/// and never invent a placement — but nothing verified it, so a hallucinated "Saturn in
/// the 7th" read exactly like a real one. This extracts the planet-in-house and dasha
/// claims the text actually makes and compares them with the engine's output.
/// </para>
/// <para>
/// Deliberately conservative: it only flags claims it can parse unambiguously, because
/// a false accusation would send a correct reading into a pointless rewrite loop.
/// </para>
/// </summary>
public static class ChartGrounding
{
    /// <summary>English and Burmese names for each planet, so claims are matched in either language.</summary>
    private static readonly Dictionary<string, string[]> PlanetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sun"] = new[] { "sun", "surya", "တနင်္ဂနွေ", "သူရိယ" },
        ["Moon"] = new[] { "moon", "chandra", "တနင်္လာ", "စန္ဒြ" },
        ["Mars"] = new[] { "mars", "mangala", "kuja", "အင်္ဂါ" },
        ["Mercury"] = new[] { "mercury", "budha", "ဗုဒ္ဓဟူး" },
        ["Jupiter"] = new[] { "jupiter", "guru", "brihaspati", "ကြာသပတေး" },
        ["Venus"] = new[] { "venus", "shukra", "သောကြာ" },
        ["Saturn"] = new[] { "saturn", "shani", "စနေ" },
        ["Rahu"] = new[] { "rahu", "ရာဟု" },
        ["Ketu"] = new[] { "ketu", "ကိတ်" },
    };

    // "Saturn in the 7th house" / "Saturn in house 7" / "Saturn is placed in the 10th".
    private static readonly Regex LatinPlanetHouse = new(
        @"(?<planet>[A-Za-z]+)\b[^.\n]{0,40}?\b(?:in|placed in|occupies|occupying)\s+(?:the\s+)?(?:house\s+)?(?<house>\d{1,2})(?:st|nd|rd|th)?\s*(?:house)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Burmese: "စနေသည် ၇ တန်တွင်" — planet, then an Arabic or Burmese numeral, then တန်/အိမ်.
    private static readonly Regex BurmesePlanetHouse = new(
        @"(?<planet>[\u1000-\u109F]+)[^။\n]{0,20}?(?<house>[\u1040-\u10490-9]{1,2})\s*(?:တန်|အိမ်)",
        RegexOptions.Compiled);

    /// <summary>Every planet-in-house claim in the text that contradicts the chart.</summary>
    public static List<GroundingIssue> Check(string text, AiReadingRequestDto chart)
    {
        var issues = new List<GroundingIssue>();
        if (string.IsNullOrWhiteSpace(text) || chart.Placements.Count == 0) return issues;

        var actual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in chart.Placements)
        {
            var canonical = Canonical(p.Planet);
            if (canonical is not null && p.House is > 0 and <= 12) actual[canonical] = p.House;
        }
        if (actual.Count == 0) return issues;

        foreach (var (planet, house, claim) in ExtractClaims(text))
        {
            if (!actual.TryGetValue(planet, out var trueHouse)) continue;   // not in the chart summary
            if (trueHouse != house)
                issues.Add(new GroundingIssue(claim, $"{planet} is in house {trueHouse}, not house {house}."));
        }

        // A dasha lord the engine never reported is the other high-impact fabrication.
        foreach (var (label, value) in new[]
                 {
                     ("Mahadasha", chart.Mahadasha),
                     ("Antardasha", chart.Antardasha),
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var named = NamedDashaLord(text, label);
            if (named is not null && Canonical(named) is { } c && Canonical(value) is { } expected && c != expected)
                issues.Add(new GroundingIssue($"{label}: {named}", $"The {label} is {value}, not {named}."));
        }

        return issues;
    }

    private static IEnumerable<(string Planet, int House, string Claim)> ExtractClaims(string text)
    {
        foreach (Match m in LatinPlanetHouse.Matches(text))
        {
            var planet = Canonical(m.Groups["planet"].Value);
            if (planet is null) continue;
            if (int.TryParse(m.Groups["house"].Value, out var h) && h is > 0 and <= 12)
                yield return (planet, h, m.Value.Trim());
        }

        foreach (Match m in BurmesePlanetHouse.Matches(text))
        {
            var planet = Canonical(m.Groups["planet"].Value);
            if (planet is null) continue;
            var h = ParseNumeral(m.Groups["house"].Value);
            if (h is > 0 and <= 12) yield return (planet, h, m.Value.Trim());
        }
    }

    /// <summary>The planet named immediately after a dasha label, if the text names one.</summary>
    private static string? NamedDashaLord(string text, string label)
    {
        var m = Regex.Match(text, label + @"[^\w\n]{0,12}(?:is|:|—|-)?\s*(?<lord>[A-Za-z\u1000-\u109F]+)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var lord = m.Groups["lord"].Value;
        return Canonical(lord) is null ? null : lord;
    }

    /// <summary>
    /// Maps a known alias (English, Sanskrit or Burmese) to the engine's planet name.
    /// <para>
    /// Latin aliases must match the whole token: substring matching would read "Sunday"
    /// as the Sun and "Marshall" as Mars, and a phantom contradiction is worse than a
    /// missed one — it sends a correct reading into a rewrite. Burmese has no word
    /// boundaries, so those aliases do match as substrings.
    /// </para>
    /// </summary>
    private static string? Canonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('*', '_', '"', '\'', '.', ',');

        foreach (var (canonical, aliases) in PlanetNames)
            foreach (var alias in aliases)
            {
                var burmese = alias[0] >= '\u1000';
                if (burmese ? s.Contains(alias, StringComparison.Ordinal)
                            : s.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return canonical;
            }

        return null;
    }

    /// <summary>Burmese digits (၀-၉) and Arabic digits both appear in generated text.</summary>
    private static int ParseNumeral(string s)
    {
        var value = 0;
        foreach (var c in s)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= '\u1040' and <= '\u1049' => c - '\u1040',
                _ => -1,
            };
            if (digit < 0) return -1;
            value = value * 10 + digit;
        }
        return value;
    }

    /// <summary>A correction note the model can act on, listing what it got wrong.</summary>
    public static string BuildCorrection(IEnumerable<GroundingIssue> issues)
    {
        var lines = issues.Select(i => $"  - \"{Shorten(i.Claim)}\" → {i.Reason}");
        return "The following statements CONTRADICT the computed chart. Rewrite those sentences "
             + "so they match the chart exactly, and change nothing else:\n"
             + string.Join("\n", lines);
    }

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
