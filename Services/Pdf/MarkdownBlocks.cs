using System.Text.RegularExpressions;

namespace PortfolioApi.Services.Pdf;

/// <summary>Kinds of block the reading's markdown can produce.</summary>
public enum MdBlockKind { Heading1, Heading2, Heading3, Paragraph, Bullet, Numbered, Quote, Rule }

/// <summary>One rendered block. <see cref="Spans"/> carries inline bold/italic runs.</summary>
public sealed class MdBlock
{
    public MdBlockKind Kind { get; init; }
    public List<MdSpan> Spans { get; init; } = new();

    /// <summary>1-based position within its list, so numbered items keep the author's
    /// sequence instead of collapsing to an anonymous dash.</summary>
    public int Ordinal { get; init; }

    public string PlainText => string.Concat(Spans.Select(s => s.Text));
}

public sealed record MdSpan(string Text, bool Bold, bool Italic);

/// <summary>
/// A deliberately small Markdown reader for the subset the reading prompt emits:
/// ATX headings, paragraphs, bullet and numbered lists, block quotes, horizontal
/// rules, and inline bold/italic. Anything else degrades to plain text rather than
/// leaking raw syntax into the PDF.
/// <para>
/// A full CommonMark parser would be a heavier dependency than the content warrants,
/// and the input is machine-generated from a prompt we control.
/// </para>
/// </summary>
public static class MarkdownBlocks
{
    private static readonly Regex InlineRe = new(@"(\*\*\*.+?\*\*\*|\*\*.+?\*\*|__.+?__|\*.+?\*|_.+?_|`.+?`)", RegexOptions.Compiled);
    private static readonly Regex NumberedRe = new(@"^\s{0,3}\d+[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRe = new(@"^\s{0,3}[-*+•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRe = new(@"^\s{0,3}([-*_])\s*(\1\s*){2,}$", RegexOptions.Compiled);

    public static List<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var inFence = false;
        var ordinal = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MdBlock { Kind = MdBlockKind.Paragraph, Spans = ParseInline(string.Join(" ", paragraph)) });
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Code fences carry no meaning in a reading; keep the contents as prose.
            if (line.TrimStart().StartsWith("```"))
            {
                FlushParagraph();
                inFence = !inFence;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }

            if (!inFence && RuleRe.IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MdBlock { Kind = MdBlockKind.Rule });
                continue;
            }

            if (!inFence && line.TrimStart().StartsWith('#'))
            {
                FlushParagraph();
                var trimmed = line.TrimStart();
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var text = trimmed[level..].Trim();
                blocks.Add(new MdBlock
                {
                    Kind = level <= 1 ? MdBlockKind.Heading1 : level == 2 ? MdBlockKind.Heading2 : MdBlockKind.Heading3,
                    Spans = ParseInline(text),
                });
                continue;
            }

            if (!inFence && line.TrimStart().StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MdBlock { Kind = MdBlockKind.Quote, Spans = ParseInline(line.TrimStart()[1..].Trim()) });
                continue;
            }

            if (!inFence)
            {
                var numbered = NumberedRe.Match(line);
                if (numbered.Success)
                {
                    FlushParagraph();
                    // Renumber from the run rather than trusting the source digits: models
                    // routinely emit "1." for every item in a list.
                    ordinal = blocks.Count > 0 && blocks[^1].Kind == MdBlockKind.Numbered ? ordinal + 1 : 1;
                    blocks.Add(new MdBlock
                    {
                        Kind = MdBlockKind.Numbered,
                        Ordinal = ordinal,
                        Spans = ParseInline(numbered.Groups[1].Value),
                    });
                    continue;
                }

                var bullet = BulletRe.Match(line);
                if (bullet.Success)
                {
                    FlushParagraph();
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Bullet, Spans = ParseInline(bullet.Groups[1].Value) });
                    continue;
                }
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>Splits a line into bold/italic runs, stripping the markers.</summary>
    public static List<MdSpan> ParseInline(string text)
    {
        var spans = new List<MdSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        var last = 0;
        foreach (Match m in InlineRe.Matches(text))
        {
            if (m.Index > last)
                spans.Add(new MdSpan(text[last..m.Index], false, false));

            var token = m.Value;
            if (token.StartsWith("***") && token.Length > 6)
                spans.Add(new MdSpan(token[3..^3], true, true));
            else if ((token.StartsWith("**") || token.StartsWith("__")) && token.Length > 4)
                spans.Add(new MdSpan(token[2..^2], true, false));
            else if (token.StartsWith('`') && token.Length > 2)
                spans.Add(new MdSpan(token[1..^1], false, false));
            else if (token.Length > 2)
                spans.Add(new MdSpan(token[1..^1], false, true));
            else
                spans.Add(new MdSpan(token, false, false));

            last = m.Index + m.Length;
        }

        if (last < text.Length)
            spans.Add(new MdSpan(text[last..], false, false));

        return spans;
    }
}
