using System.Text;

namespace Agwinterm.Core;

public sealed record HudTextRange(int Start, int Length, bool Bold = false, bool Italic = false, bool Code = false, float Size = 1);
public sealed record HudDocument(string Text, IReadOnlyList<HudTextRange> Ranges);

/// <summary>A bounded native presentation subset, never HTML or executable content.</summary>
public static class HudMarkdown
{
    public const int MaxScalars = 4096, MaxLines = 128;
    public static HudDocument Parse(string source)
    {
        source = source.Normalize().Replace("\r\n", "\n").Replace('\r', '\n');
        if (source.EnumerateRunes().Count() > MaxScalars || source.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            throw new ArgumentException("HUD Markdown exceeds 4096 scalars or contains unsupported controls");
        var lines = source.Split('\n');
        if (lines.Length > MaxLines) throw new ArgumentException("HUD Markdown exceeds 128 lines");
        var text = new StringBuilder(); var ranges = new List<HudTextRange>(); bool code = false;
        void AppendLine(string value, bool bold = false, bool italic = false, bool mono = false, float size = 1)
        {
            if (text.Length > 0) text.Append('\n');
            int start = text.Length;
            if (mono) text.Append(value.Replace("\t", "    "));
            else Inline(value, text, ranges);
            if (text.Length > start && (bold || italic || mono || size != 1)) ranges.Add(new(start, text.Length - start, bold, italic, mono, size));
        }
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i], trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            if (code) { AppendLine(line, mono: true); continue; }
            if (i + 1 < lines.Length && line.Contains('|') && IsTableDivider(lines[i + 1]))
            {
                var header = Cells(line);
                if (header.Length > 8 || Cells(lines[i + 1]).Length != header.Length) throw new ArgumentException("HUD table requires matching columns, at most 8");
                var rows = new List<string[]> { header }; i++;
                while (i + 1 < lines.Length && lines[i + 1].Contains('|'))
                {
                    var cells = Cells(lines[++i]);
                    if (rows.Count == 32 || cells.Length != header.Length) throw new ArgumentException("HUD table requires matching columns and at most 32 rows");
                    rows.Add(cells);
                }
                // Cap alignment padding independently of input: long cells wrap instead of
                // multiplying their width across every row of a table.
                var widths = Enumerable.Range(0, header.Length).Select(c => Math.Min(64, rows.Max(r => r[c].EnumerateRunes().Count()))).ToArray();
                for (int row = 0; row < rows.Count; row++)
                    AppendLine(string.Join(" │ ", rows[row].Select((cell, c) => c == header.Length - 1 ? cell :
                        cell + new string(' ', Math.Max(0, widths[c] - cell.EnumerateRunes().Count())))), bold: row == 0, mono: true);
                continue;
            }
            int heading = trimmed.TakeWhile(c => c == '#').Count();
            if (heading is >= 1 and <= 6 && trimmed.Length > heading && trimmed[heading] == ' ')
            { AppendLine(trimmed[(heading + 1)..], bold: true, size: heading == 1 ? 1.5f : heading == 2 ? 1.3f : 1.15f); continue; }
            string compact = trimmed.Replace(" ", "");
            if (compact.Length >= 3 && (compact.All(c => c == '-') || compact.All(c => c == '*') || compact.All(c => c == '_')))
            { AppendLine("────────────", mono: true); continue; }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal)) { AppendLine("│ " + trimmed[2..], italic: true); continue; }
            if (trimmed.Length > 1 && trimmed[1] == ' ' && trimmed[0] is '-' or '*' or '+')
            { AppendLine("• " + trimmed[2..]); continue; }
            AppendLine(line);
        }
        return new(text.ToString(), ranges.AsReadOnly());
    }
    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("\t", "    ")).ToArray();
    private static bool IsTableDivider(string line)
    {
        var cells = Cells(line);
        return line.Contains('|') && cells.All(c => c.Trim(':').Length >= 3 && c.Trim(':').All(ch => ch == '-'));
    }
    private static void Inline(string value, StringBuilder text, List<HudTextRange> ranges)
    {
        for (int i = 0; i < value.Length;)
        {
            if (value[i] == '\\' && i + 1 < value.Length && "*`_\\#|".Contains(value[i + 1]))
            { text.Append(value[i + 1]); i += 2; continue; }
            string? marker = value[i] == '`' ? "`" : value[i] is '*' or '_' ?
                i + 1 < value.Length && value[i + 1] == value[i] ? new string(value[i], 2) : value[i].ToString() : null;
            if (marker is not null)
            {
                int end = value.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                if (end > i + marker.Length)
                {
                    int start = text.Length; text.Append(value.AsSpan(i + marker.Length, end - i - marker.Length));
                    ranges.Add(new(start, text.Length - start, Bold: marker.Length == 2, Italic: marker is "*" or "_", Code: marker == "`"));
                    i = end + marker.Length; continue;
                }
            }
            text.Append(value[i++]);
        }
    }
}
