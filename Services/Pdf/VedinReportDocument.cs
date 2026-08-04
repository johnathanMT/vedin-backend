using PortfolioApi.DTOs.Astrology;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PortfolioApi.Services.Pdf;

/// <summary>Everything the report needs, assembled by the caller.</summary>
public sealed class VedinReportModel
{
    public string QuerentName { get; init; } = string.Empty;
    public string? BirthDate { get; init; }
    public string? BirthTime { get; init; }
    public string? Location { get; init; }
    public bool BirthTimeUnknown { get; init; }
    public AiReadingRequestDto? Chart { get; init; }
    public string? ReadingMarkdown { get; init; }
    public string? Model { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public bool Burmese { get; init; } = true;
}

/// <summary>
/// The premium report: cover, contents, chart plate, placements, dasha timeline,
/// Ashtakavarga strengths and the Sayar-approved reading.
/// <para>
/// This replaces the old hand-rolled PDF 1.4 writer, which emitted a single
/// Helvetica page and stripped every non-ASCII character — meaning it could not
/// render a single Burmese glyph, in a product whose readings are written in Burmese.
/// </para>
/// </summary>
public sealed class VedinReportDocument : IDocument
{
    private static readonly string[] SignNames =
    {
        "Aries", "Taurus", "Gemini", "Cancer", "Leo", "Virgo",
        "Libra", "Scorpio", "Sagittarius", "Capricorn", "Aquarius", "Pisces",
    };

    // Two letters collide (Cancer/Capricorn, Sagittarius/Scorpio), which would mislabel
    // half the plate; the conventional three-letter forms are unambiguous.
    private static readonly string[] SignShort =
    {
        "Ari", "Tau", "Gem", "Can", "Leo", "Vir",
        "Lib", "Sco", "Sag", "Cap", "Aqu", "Pis",
    };

    // South-Indian plate: signs sit in fixed cells of a 4×4 grid, centre left empty.
    // row/col → sign index, or null for the two centre rows.
    private static readonly int?[,] Plate =
    {
        { 11, 0, 1, 2 },
        { 10, null, null, 3 },
        { 9, null, null, 4 },
        { 8, 7, 6, 5 },
    };

    private readonly VedinReportModel _m;

    public VedinReportDocument(VedinReportModel model) => _m = model;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"Vedin — {(_m.QuerentName.Length > 0 ? _m.QuerentName : "Vedic Reading")}",
        Author = "Sayar Bhone Min Thike Din",
        Subject = "Vedic astrology reading",
        Creator = "Vedin",
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    private string T(string en, string mm) => _m.Burmese ? mm : en;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(0);
            page.PageColor(VedinTheme.Parchment);
            page.DefaultTextStyle(VedinTheme.Body());

            // Header/footer rather than one tall column: a column that Extend()s inside
            // page content overflows and spills a phantom second cover page.
            page.Header().Height(10).Background(VedinTheme.Gold);
            page.Content().Element(ComposeCover);
            page.Footer().Column(col =>
            {
                col.Item().PaddingHorizontal(2.4f, Unit.Centimetre).PaddingBottom(2.2f, Unit.Centimetre)
                    .Text(T(
                        "Sidereal · Lahiri ayanamsa · Whole-Sign houses",
                        "နက္ခတ် · Lahiri ayanamsa · Whole-Sign houses"))
                    .Style(VedinTheme.Mono(8.5f));
                col.Item().Height(10).Background(VedinTheme.Violet);
            });
        });

        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.8f, Unit.Centimetre);
            page.PageColor(VedinTheme.Parchment);
            page.DefaultTextStyle(VedinTheme.Body());

            page.Header().Element(ComposeRunningHeader);
            page.Content().PaddingVertical(12).Element(ComposeBody);
            page.Footer().Element(ComposeFooter);
        });
    }

    // ── Cover ───────────────────────────────────────────────────────────────────
    private void ComposeCover(IContainer c)
    {
        c.Column(col =>
        {
            col.Item().PaddingHorizontal(2.4f, Unit.Centimetre).PaddingTop(3.4f, Unit.Centimetre).Column(inner =>
            {
                // Letter-spacing separates Burmese glyph clusters, so it is applied to
                // the Latin eyebrow only.
                inner.Item().Text(T("VEDIC ASTROLOGY", "ဗေဒင် ဟောစာတမ်း"))
                    .Style(_m.Burmese ? VedinTheme.Label() : VedinTheme.Label().LetterSpacing(0.28f))
                    .FontSize(10).FontColor(VedinTheme.Gold);

                inner.Item().PaddingTop(6).Text(_m.QuerentName.Length > 0 ? _m.QuerentName : T("Your Reading", "သင့် ဟောစာတမ်း"))
                    .Style(VedinTheme.Heading(30)).FontColor(VedinTheme.Ink);

                inner.Item().PaddingTop(10).Width(160).Height(2).Background(VedinTheme.Gold);

                inner.Item().PaddingTop(22).Element(CoverFacts);

                inner.Item().PaddingTop(28).Text(T(
                        "Prepared personally by Sayar Bhone Min Thike Din",
                        "ဆရာ ဘုန်းမင်းသိုက်ဒင် မှ ကိုယ်တိုင် ပြင်ဆင်ပေးပါသည်"))
                    .Style(VedinTheme.Body(11)).FontColor(VedinTheme.InkSoft);

                inner.Item().PaddingTop(4).Text(_m.GeneratedAt.ToString("d MMMM yyyy"))
                    .Style(VedinTheme.Mono());
            });
        });
    }

    private void CoverFacts(IContainer c)
    {
        var ch = _m.Chart;
        var facts = new List<(string Label, string? Value)>
        {
            (T("Lagna (Ascendant)", "လဂ်"), ch?.Ascendant),
            (T("Moon sign", "စန်းရာသီ"), ch?.MoonSign),
            (T("Sun sign", "နေရာသီ"), ch?.SunSign),
            (T("Nay Nan", "နေ့နံ"), ch?.NayNan),
            (T("Current Mahadasha", "လက်ရှိ မဟာဒသာ"), ch?.Mahadasha),
            (T("Birth", "မွေးဖွားချိန်"), FormatBirth()),
            (T("Place", "မွေးဖွားရာ"), _m.Location),
        };

        c.Background(VedinTheme.Panel).Border(1).BorderColor(VedinTheme.Rule).Padding(16).Column(col =>
        {
            foreach (var (label, value) in facts.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
            {
                col.Item().PaddingVertical(3).Row(row =>
                {
                    row.ConstantItem(150).Text(label).Style(VedinTheme.Label()).FontSize(9);
                    row.RelativeItem().Text(value!).Style(VedinTheme.Body(11)).SemiBold();
                });
            }
        });
    }

    private string? FormatBirth()
    {
        if (string.IsNullOrWhiteSpace(_m.BirthDate)) return null;
        var time = _m.BirthTimeUnknown
            ? T("(time unknown — noon used)", "(အချိန် မသိ — မွန်းတည့် သုံးထား)")
            : _m.BirthTime;
        return string.IsNullOrWhiteSpace(time) ? _m.BirthDate : $"{_m.BirthDate} · {time}";
    }

    // ── Running header / footer ─────────────────────────────────────────────────
    private void ComposeRunningHeader(IContainer c) =>
        c.PaddingBottom(6).BorderBottom(1).BorderColor(VedinTheme.Rule).Row(row =>
        {
            row.RelativeItem().Text(T("Vedin · Vedic Reading", "Vedin · ဗေဒင် ဟောစာတမ်း"))
                .Style(VedinTheme.Mono(8.5f)).FontColor(VedinTheme.Gold);
            row.ConstantItem(220).AlignRight()
                .Text(_m.QuerentName.Length > 0 ? _m.QuerentName : "—")
                .Style(VedinTheme.Mono(8.5f));
        });

    private void ComposeFooter(IContainer c) =>
        c.PaddingTop(6).BorderTop(1).BorderColor(VedinTheme.Rule).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                t.DefaultTextStyle(VedinTheme.Mono(8));
                t.Span(T("Guidance for reflection — not a substitute for professional advice.",
                    "ဆင်ခြင်သုံးသပ်ရန် လမ်းညွှန်ချက်သာဖြစ်၍ ကျွမ်းကျင်သူ၏ အကြံဉာဏ်ကို အစားထိုးခြင်း မဟုတ်ပါ။"));
            });
            row.ConstantItem(90).AlignRight().Text(t =>
            {
                t.DefaultTextStyle(VedinTheme.Mono(8));
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });

    // ── Body ────────────────────────────────────────────────────────────────────
    private void ComposeBody(IContainer c) =>
        c.Column(col =>
        {
            col.Spacing(18);
            col.Item().Element(ComposeContents);
            col.Item().Element(ComposeChartPlate);

            if (_m.Chart?.Placements is { Count: > 0 })
                col.Item().Element(ComposePlacements);

            col.Item().Element(ComposeDasha);

            if (_m.Chart?.SarvashtakavargaBySign is { Count: 12 })
                col.Item().Element(ComposeAshtakavarga);

            if (_m.Chart?.Yogas is { Count: > 0 })
                col.Item().Element(ComposeYogas);

            col.Item().PageBreak();
            col.Item().Element(ComposeReading);
            col.Item().Element(ComposeColophon);
        });

    private void SectionTitle(IContainer c, string text) =>
        c.PaddingBottom(6).BorderBottom(1).BorderColor(VedinTheme.Rule)
            .Text(text).Style(VedinTheme.Heading(14));

    private void ComposeContents(IContainer c) =>
        c.Column(col =>
        {
            col.Item().Element(x => SectionTitle(x, T("Contents", "မာတိကာ")));

            var entries = new List<string>
            {
                T("1 · Birth chart plate", "၁ · ဇာတာခွင်"),
                T("2 · Planetary placements", "၂ · ဂြိုဟ်တည်နေရာများ"),
                T("3 · Dasha periods", "၃ · ဒသာ ကာလများ"),
                T("4 · Ashtakavarga strengths", "၄ · အဋ္ဌကဝါဂ် အား"),
                T("5 · Your reading", "၅ · သင့် ဟောစာတမ်း"),
            };

            foreach (var e in entries)
                col.Item().PaddingTop(5).Text(e).Style(VedinTheme.Body(10.5f));
        });

    /// <summary>
    /// The chart is drawn as a real table rather than a screenshot of the DOM, so it
    /// stays vector, selectable and crisp at any zoom or print size.
    /// </summary>
    private void ComposeChartPlate(IContainer c) =>
        c.Column(col =>
        {
            col.Item().Element(x => SectionTitle(x, T("1 · Birth chart plate (D1 Rasi)", "၁ · ဇာတာခွင် (D1 ရာသီ)")));

            var byHouse = BuildSignPlanets();
            var lagnaSign = ResolveLagnaSign();

            col.Item().PaddingTop(10).AlignCenter().Width(15, Unit.Centimetre).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    for (var i = 0; i < 4; i++) cd.RelativeColumn();
                });

                for (var r = 0; r < 4; r++)
                {
                    for (var cIdx = 0; cIdx < 4; cIdx++)
                    {
                        var sign = Plate[r, cIdx];
                        var cell = table.Cell().Row((uint)r + 1).Column((uint)cIdx + 1);

                        if (sign is null)
                        {
                            // Centre well — carries the chart caption, as in the app.
                            if (r == 1 && cIdx == 1)
                            {
                                cell.ColumnSpan(2).RowSpan(2).Border(1).BorderColor(VedinTheme.Rule)
                                    .Background(VedinTheme.Panel).Padding(8).AlignMiddle().AlignCenter().Column(inner =>
                                    {
                                        inner.Item().AlignCenter().Text("D1 · Rasi").Style(VedinTheme.Mono(10));
                                        inner.Item().AlignCenter().PaddingTop(3)
                                            .Text(_m.Chart?.Ascendant ?? "—").Style(VedinTheme.Body(10)).SemiBold();
                                    });
                            }
                            continue;
                        }

                        var isLagna = sign == lagnaSign;
                        var planets = byHouse.TryGetValue(sign.Value, out var list) ? list : new List<string>();

                        cell.Border(1).BorderColor(VedinTheme.Rule)
                            .Background(isLagna ? Color.FromHex("#EFE6CB") : VedinTheme.Parchment)
                            .MinHeight(74).Padding(5).Column(inner =>
                            {
                                inner.Item().Row(row =>
                                {
                                    row.RelativeItem().Text(SignShort[sign.Value])
                                        .Style(VedinTheme.Mono(8)).FontColor(VedinTheme.InkSoft);
                                    if (isLagna)
                                        row.ConstantItem(20).AlignRight().Text("La")
                                            .Style(VedinTheme.Mono(8)).FontColor(VedinTheme.Violet).Bold();
                                });

                                foreach (var p in planets)
                                    inner.Item().PaddingTop(1).AlignCenter()
                                        .Text(p).Style(VedinTheme.Body(9.5f)).SemiBold();
                            });
                    }
                }
            });
        });

    private int ResolveLagnaSign()
    {
        var asc = _m.Chart?.Ascendant;
        if (string.IsNullOrWhiteSpace(asc)) return -1;
        for (var i = 0; i < SignNames.Length; i++)
            if (asc.Contains(SignNames[i], StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Groups the placements by the sign they occupy, for the plate cells.</summary>
    private Dictionary<int, List<string>> BuildSignPlanets()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var p in _m.Chart?.Placements ?? new List<PlacementDto>())
        {
            var idx = Array.FindIndex(SignNames, s => p.Sign.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            var label = Abbrev(p.Planet) + (p.Retrograde ? " \u211E" : string.Empty);
            (map.TryGetValue(idx, out var list) ? list : map[idx] = new List<string>()).Add(label);
        }
        return map;
    }

    private static string Abbrev(string planet) => planet switch
    {
        "Sun" => "Su", "Moon" => "Mo", "Mars" => "Ma", "Mercury" => "Me",
        "Jupiter" => "Ju", "Venus" => "Ve", "Saturn" => "Sa",
        "Rahu" => "Ra", "Ketu" => "Ke",
        _ => planet.Length >= 2 ? planet[..2] : planet,
    };

    private void ComposePlacements(IContainer c) =>
        c.Column(col =>
        {
            col.Item().Element(x => SectionTitle(x, T("2 · Planetary placements", "၂ · ဂြိုဟ်တည်နေရာများ")));

            col.Item().PaddingTop(8).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(2.0f);
                    cd.RelativeColumn(2.2f);
                    cd.RelativeColumn(1.1f);
                    cd.RelativeColumn(2.4f);
                    cd.RelativeColumn(2.0f);
                });

                table.Header(h =>
                {
                    void Th(string s) => h.Cell().Background(VedinTheme.Panel).Padding(5)
                        .Text(s).Style(VedinTheme.Label()).FontSize(8.5f);

                    Th(T("Planet", "ဂြိုဟ်"));
                    Th(T("Sign", "ရာသီ"));
                    Th(T("House", "အိမ်"));
                    Th(T("Nakshatra", "နက္ခတ်"));
                    Th(T("Dignity", "အင်အား"));
                });

                foreach (var p in _m.Chart!.Placements)
                {
                    void Td(string s, TextStyle? style = null) => table.Cell()
                        .BorderBottom(1).BorderColor(VedinTheme.Rule).Padding(5)
                        .Text(s).Style(style ?? VedinTheme.Body(9.5f));

                    Td(p.Planet + (p.Retrograde ? " \u211E" : string.Empty), VedinTheme.Body(9.5f).SemiBold());
                    Td(p.Sign);
                    Td(p.House > 0 ? p.House.ToString() : "—");
                    Td(p.Nakshatra ?? "—");
                    Td(p.Dignity ?? "—", DignityStyle(p.Dignity));
                }
            });
        });

    private static TextStyle DignityStyle(string? dignity) => (dignity ?? string.Empty).ToLowerInvariant() switch
    {
        "exalted" => VedinTheme.Body(9.5f).FontColor(VedinTheme.Jade).SemiBold(),
        "debilitated" => VedinTheme.Body(9.5f).FontColor(VedinTheme.Coral).SemiBold(),
        "own" => VedinTheme.Body(9.5f).FontColor(VedinTheme.Violet),
        _ => VedinTheme.Body(9.5f),
    };

    private void ComposeDasha(IContainer c) =>
        c.Column(col =>
        {
            col.Item().Element(x => SectionTitle(x, T("3 · Dasha periods", "၃ · ဒသာ ကာလများ")));

            var ch = _m.Chart;
            var rows = new List<(string, string?)>
            {
                (T("Mahadasha", "မဟာဒသာ"), ch?.Mahadasha),
                (T("Antardasha (bhukti)", "အန္တရ်ဒသာ (ဘုတ္တိ)"), ch?.Antardasha),
                (T("Pratyantardasha", "ပရత္ယန္တရ်ဒသာ"), ch?.Pratyantardasha),
                (T("Window", "ကာလ"), ch?.DashaWindow),
                (T("Sade Sati", "သာဓေသတီ"), ch?.SadeSatiStatus),
            };

            col.Item().PaddingTop(8).Table(table =>
            {
                table.ColumnsDefinition(cd => { cd.RelativeColumn(1.2f); cd.RelativeColumn(2f); });
                foreach (var (label, value) in rows.Where(r => !string.IsNullOrWhiteSpace(r.Item2)))
                {
                    table.Cell().BorderBottom(1).BorderColor(VedinTheme.Rule).Padding(5)
                        .Text(label).Style(VedinTheme.Label()).FontSize(9);
                    table.Cell().BorderBottom(1).BorderColor(VedinTheme.Rule).Padding(5)
                        .Text(value!).Style(VedinTheme.Body(10)).SemiBold();
                }
            });
        });

    private void ComposeAshtakavarga(IContainer c) =>
        c.Column(col =>
        {
            col.Item().Element(x => SectionTitle(x, T("4 · Ashtakavarga strengths", "၄ · အဋ္ဌကဝါဂ် အား")));

            var values = _m.Chart!.SarvashtakavargaBySign!;
            var max = Math.Max(values.Max(), 1);

            col.Item().PaddingTop(8).Column(inner =>
            {
                for (var i = 0; i < 12; i++)
                {
                    var v = values[i];
                    inner.Item().PaddingVertical(2).Row(row =>
                    {
                        row.ConstantItem(88).Text(SignNames[i]).Style(VedinTheme.Body(9.5f));
                        row.ConstantItem(30).Text(v.ToString()).Style(VedinTheme.Mono(9));
                        // A bar chart drawn from two boxes: no image, no chart library.
                        row.RelativeItem().Height(9).AlignMiddle().Row(bar =>
                        {
                            bar.RelativeItem(Math.Max(v, 1)).Height(9).Background(VedinTheme.Violet);
                            bar.RelativeItem(Math.Max(max - v, 1)).Height(9).Background(VedinTheme.Panel);
                        });
                    });
                }
            });

            if (!string.IsNullOrWhiteSpace(_m.Chart.AshtakavargaNotes))
                col.Item().PaddingTop(8).Text(_m.Chart.AshtakavargaNotes!).Style(VedinTheme.Mono(9));
        });

    private void ComposeYogas(IContainer c) =>
        c.Column(col =>
        {
            col.Item().Element(x => SectionTitle(x, T("Active yogas", "ဖွဲ့စည်းမှု (ယောဂ) များ")));
            col.Item().PaddingTop(6).Column(inner =>
            {
                foreach (var y in _m.Chart!.Yogas!)
                    inner.Item().PaddingVertical(1.5f).Text("• " + y).Style(VedinTheme.Body(10));
            });
        });

    private void ComposeReading(IContainer c) =>
        c.Column(col =>
        {
            col.Item().Element(x => SectionTitle(x, T("5 · Your reading", "၅ · သင့် ဟောစာတမ်း")));

            if (string.IsNullOrWhiteSpace(_m.ReadingMarkdown))
            {
                col.Item().PaddingTop(10).Text(T(
                        "Your detailed reading has not been approved yet. It will appear here once the Sayar releases it.",
                        "အသေးစိတ် ဟောစာတမ်းကို ဆရာမှ အတည်မပြုရသေးပါ။ အတည်ပြုပြီးပါက ဤနေရာတွင် ပါဝင်လာပါမည်။"))
                    .Style(VedinTheme.Body(10.5f)).FontColor(VedinTheme.InkSoft);
                return;
            }

            col.Item().PaddingTop(4).Column(inner =>
            {
                foreach (var block in MarkdownBlocks.Parse(_m.ReadingMarkdown!))
                    RenderBlock(inner, block);
            });
        });

    private void RenderBlock(ColumnDescriptor col, MdBlock block)
    {
        switch (block.Kind)
        {
            case MdBlockKind.Rule:
                col.Item().PaddingVertical(8).LineHorizontal(1).LineColor(VedinTheme.Rule);
                return;

            case MdBlockKind.Heading1:
            case MdBlockKind.Heading2:
            case MdBlockKind.Heading3:
                var size = block.Kind == MdBlockKind.Heading1 ? 15f : block.Kind == MdBlockKind.Heading2 ? 13f : 11.5f;
                col.Item().PaddingTop(12).PaddingBottom(3)
                    .Text(block.PlainText).Style(VedinTheme.Heading(size));
                return;

            case MdBlockKind.Quote:
                col.Item().PaddingVertical(5).BorderLeft(3).BorderColor(VedinTheme.Gold)
                    .Background(VedinTheme.Panel).PaddingLeft(10).PaddingVertical(6)
                    .Text(t => WriteSpans(t, block, VedinTheme.Body(10).Italic()));
                return;

            case MdBlockKind.Bullet:
            case MdBlockKind.Numbered:
                col.Item().PaddingTop(3).Row(row =>
                {
                    row.ConstantItem(18).AlignTop()
                        .Text(block.Kind == MdBlockKind.Bullet ? "•" : $"{block.Ordinal}.")
                        .Style(VedinTheme.Body(10.5f)).FontColor(VedinTheme.Gold);
                    row.RelativeItem().Text(t => WriteSpans(t, block, VedinTheme.Body(10.5f)));
                });
                return;

            default:
                col.Item().PaddingTop(6).Text(t => WriteSpans(t, block, VedinTheme.Body(10.5f)));
                return;
        }
    }

    private void WriteSpans(TextDescriptor t, MdBlock block, TextStyle baseStyle)
    {
        t.DefaultTextStyle(baseStyle);

        // Burmese writes without inter-word spaces, so justification has almost nothing
        // to stretch and blows the few existing gaps wide open. Only justify Latin text.
        if (!_m.Burmese) t.Justify();

        foreach (var span in block.Spans)
        {
            var s = t.Span(span.Text);
            if (span.Bold) s.SemiBold().FontColor(VedinTheme.Ink);
            if (span.Italic) s.Italic();
        }
    }

    private void ComposeColophon(IContainer c) =>
        c.PaddingTop(22).BorderTop(1).BorderColor(VedinTheme.Rule).PaddingTop(8).Column(col =>
        {
            col.Item().Text(T("About this document", "ဤစာတမ်းအကြောင်း")).Style(VedinTheme.Label()).FontSize(9);
            col.Item().PaddingTop(4).Text(T(
                    "Computed with the Swiss Ephemeris using the Lahiri sidereal ayanamsa and Whole-Sign houses. " +
                    "The interpretation was reviewed and released by Sayar Bhone Min Thike Din. Traditional Vedic " +
                    "astrology is offered here for interest, reflection and study.",
                    "Swiss Ephemeris ဖြင့် Lahiri နက္ခတ် ayanamsa နှင့် Whole-Sign အိမ်စနစ်ကို သုံး၍ တွက်ချက်ထားပါသည်။ " +
                    "ဟောကိန်းကို ဆရာ ဘုန်းမင်းသိုက်ဒင် မှ စစ်ဆေး အတည်ပြုပေးထားပါသည်။ ရိုးရာ ဗေဒင်ပညာကို စိတ်ဝင်စားမှု၊ " +
                    "ဆင်ခြင်သုံးသပ်မှုနှင့် လေ့လာမှုအတွက် တင်ဆက်ပါသည်။"))
                .Style(VedinTheme.Mono(8.5f));

            if (!string.IsNullOrWhiteSpace(_m.Model))
                col.Item().PaddingTop(4).Text($"Model: {_m.Model} · {_m.GeneratedAt:yyyy-MM-dd HH:mm} UTC")
                    .Style(VedinTheme.Mono(8));
        });
}
