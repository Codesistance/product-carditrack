using System.Text;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The Markdown subset a generated narrative may use — headings, paragraphs, bullet and numbered
/// lists, bold and italic — parsed into blocks the PDF lays out as formatting rather than
/// printing as punctuation.
/// </summary>
/// <remarks>
/// <para>
/// The model is asked for structure, and structure in plain text is Markdown: "## Sleep",
/// "**higher than usual**", "- steps fell". Printed verbatim those marks are noise in a document
/// a caregiver hands to a clinician, and they are the first thing a reader notices. Parsing is
/// the fix, not asking the model to stop — a prompt is a request, and this is a guarantee.
/// </para>
/// <para>
/// Anything outside the subset (tables, code, links, images) degrades to its text. There is no
/// HTML, and the output is never markup: the blocks are handed straight to the layout engine.
/// </para>
/// </remarks>
internal static class NarrativeMarkdown
{
    internal abstract record Block;

    internal sealed record Heading(int Level, IReadOnlyList<Run> Runs) : Block;

    internal sealed record Paragraph(IReadOnlyList<Run> Runs) : Block;

    /// <param name="Ordered">Numbered rather than bulleted; the numbers are regenerated on
    /// render so a model that wrote "1. 1. 1." still reads as a list.</param>
    internal sealed record ListBlock(bool Ordered, IReadOnlyList<IReadOnlyList<Run>> Items) : Block;

    internal sealed record Run(string Text, bool Bold, bool Italic);

    public static IReadOnlyList<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var paragraph = new List<string>();
        ListBlock? list = null;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            blocks.Add(new Paragraph(ParseInlines(string.Join(' ', paragraph))));
            paragraph.Clear();
        }

        void FlushList()
        {
            if (list is null)
                return;
            blocks.Add(list);
            list = null;
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || IsRule(line))
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (TryHeading(line, out var level, out var headingText))
            {
                FlushParagraph();
                FlushList();
                blocks.Add(new Heading(level, ParseInlines(headingText)));
                continue;
            }

            if (TryListItem(line, out var ordered, out var itemText))
            {
                FlushParagraph();
                if (list is null || list.Ordered != ordered)
                {
                    FlushList();
                    list = new ListBlock(ordered, new List<IReadOnlyList<Run>>());
                }
                ((List<IReadOnlyList<Run>>)list.Items).Add(ParseInlines(itemText));
                continue;
            }

            // A wrapped line inside a list item continues the item rather than opening a paragraph.
            if (list is not null && raw.Length > 0 && char.IsWhiteSpace(raw[0]))
            {
                var items = (List<IReadOnlyList<Run>>)list.Items;
                items[^1] = ParseInlines(ToText(items[^1]) + " " + line);
                continue;
            }

            FlushList();
            if (line.StartsWith('>'))
                line = line.TrimStart('>').Trim();
            paragraph.Add(line);
        }

        FlushParagraph();
        FlushList();
        return blocks;
    }

    /// <summary>The narrative with every mark removed — for a place that takes only plain text.</summary>
    public static string ToPlainText(string text) =>
        string.Join("\n\n", Parse(text).Select(block => block switch
        {
            Heading h => ToText(h.Runs),
            Paragraph p => ToText(p.Runs),
            ListBlock l => string.Join("\n", l.Items.Select(ToText)),
            _ => string.Empty
        }));

    private static string ToText(IReadOnlyList<Run> runs) => string.Concat(runs.Select(r => r.Text));

    private static bool IsRule(string line) =>
        line.Length >= 3 && (line.All(c => c == '-') || line.All(c => c == '*') || line.All(c => c == '_'));

    private static bool TryHeading(string line, out int level, out string text)
    {
        level = 0;
        while (level < line.Length && line[level] == '#')
            level++;

        if (level is 0 or > 6 || level == line.Length || line[level] != ' ')
        {
            text = string.Empty;
            return false;
        }

        text = line[(level + 1)..].Trim().TrimEnd('#').Trim();
        return text.Length > 0;
    }

    private static bool TryListItem(string line, out bool ordered, out string text)
    {
        if (line.Length > 2 && line[0] is '-' or '*' or '+' or '•' && line[1] == ' ')
        {
            ordered = false;
            text = line[2..].Trim();
            return true;
        }

        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
            digits++;

        if (digits is > 0 and <= 3 && digits + 1 < line.Length
            && line[digits] is '.' or ')' && line[digits + 1] == ' ')
        {
            ordered = true;
            text = line[(digits + 2)..].Trim();
            return true;
        }

        ordered = false;
        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Bold and italic, by the four common spellings. Marks that never close are kept as text —
    /// an asterisk the model meant literally must not swallow the rest of the sentence.
    /// </summary>
    internal static IReadOnlyList<Run> ParseInlines(string text)
    {
        var runs = new List<Run>();
        var buffer = new StringBuilder();
        var bold = false;
        var italic = false;

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            runs.Add(new Run(buffer.ToString(), bold, italic));
            buffer.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '`')
            {
                // Code spans are not a thing a health narrative needs; the backticks drop away.
                continue;
            }

            if (c is '*' or '_')
            {
                var doubled = i + 1 < text.Length && text[i + 1] == c;
                var length = doubled ? 2 : 1;
                var mark = text.Substring(i, length);

                // An underscore inside a word (snake_case, e-mail) is a character, not emphasis.
                if (c == '_' && !doubled && i > 0 && char.IsLetterOrDigit(text[i - 1])
                    && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
                {
                    buffer.Append(c);
                    continue;
                }

                var opening = doubled ? !bold : !italic;
                if (opening && text.IndexOf(mark, i + length, StringComparison.Ordinal) < 0)
                {
                    buffer.Append(mark);
                    i += length - 1;
                    continue;
                }

                Flush();
                if (doubled)
                    bold = !bold;
                else
                    italic = !italic;
                i += length - 1;
                continue;
            }

            buffer.Append(c);
        }

        Flush();
        return runs;
    }
}
