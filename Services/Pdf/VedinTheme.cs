using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PortfolioApi.Services.Pdf;

/// <summary>
/// Shared type and colour scale for the generated report.
/// <para>
/// Burmese is a complex script: it needs mark-to-base positioning and reordering that
/// only a shaping engine can do. QuestPDF renders through SkiaSharp/HarfBuzz, which
/// handles that — but only if a Myanmar-capable font is actually installed. The
/// Dockerfile installs Padauk and Noto Sans Myanmar; <see cref="Burmese"/> names them
/// in preference order and falls back to the Latin face for ASCII runs.
/// </para>
/// </summary>
public static class VedinTheme
{
    public const string Latin = "DejaVu Sans";
    public const string Burmese = "Padauk";
    public const string BurmeseFallback = "Noto Sans Myanmar";

    // Parchment/ink palette, matching the app's light theme so the PDF reads as the
    // same product rather than a generic export.
    public static readonly Color Ink = Color.FromHex("#2B2318");
    public static readonly Color InkSoft = Color.FromHex("#6B5D45");
    public static readonly Color Gold = Color.FromHex("#B0842A");
    public static readonly Color Violet = Color.FromHex("#5B4B9E");
    public static readonly Color Jade = Color.FromHex("#1F7A5A");
    public static readonly Color Coral = Color.FromHex("#B3402F");
    public static readonly Color Parchment = Color.FromHex("#FAF6EC");
    public static readonly Color Panel = Color.FromHex("#F5EEDD");
    public static readonly Color Rule = Color.FromHex("#DCCFAE");

    /// <summary>Body text style with the Myanmar fallback chain attached.</summary>
    public static TextStyle Body(float size = 10.5f) => TextStyle.Default
        .FontFamily(Burmese, BurmeseFallback, Latin, Fonts.Arial)
        .FontSize(size)
        .LineHeight(1.55f)
        .FontColor(Ink);

    public static TextStyle Heading(float size) => Body(size).Bold().FontColor(Violet);
    public static TextStyle Label() => Body(8f).FontColor(InkSoft).Light();
    public static TextStyle Mono(float size = 9f) => Body(size).FontColor(InkSoft);
}
