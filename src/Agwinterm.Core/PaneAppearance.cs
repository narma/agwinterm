using System.Text;

namespace Agwinterm.Core;

public sealed record PaneAppearance(string? Color = null, string? Text = null, int Opacity = 15,
    string? StatusColor = null, string? StatusShape = null, bool HideImage = false)
{
    public static bool ValidColor(string? value) => value is null || value.Length == 7 && value[0] == '#' && value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") == false;
    public PaneAppearance Validate()
    {
        string? text = Text?.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!ValidColor(Color) || !ValidColor(StatusColor)) throw new ArgumentException("colors require #RRGGBB");
        if (Opacity is < 0 or > 100) throw new ArgumentException("opacity must be 0..100");
        if (StatusShape is not (null or "circle" or "square" or "diamond")) throw new ArgumentException("status-shape must be circle/square/diamond");
        if (text is not null && (text.EnumerateRunes().Count() > 512 || text.Count(c => c == '\n') >= 12 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))) throw new ArgumentException("watermark allows at most 512 scalars and 12 lines, without control characters");
        return this with { Text = text };
    }
    public static PaneAppearance? Restore(PaneAppearance? value)
    { try { return value?.Validate(); } catch (ArgumentException) { return null; } }
}

public static class SystemThemeChoice
{
    public static string Select(string manual, bool follow, bool light, string? lightTheme, string? darkTheme)
    {
        string? side = light ? lightTheme : darkTheme;
        return follow && !string.IsNullOrWhiteSpace(side) ? side : manual;
    }
}
