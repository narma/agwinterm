namespace Agwinterm.Core;

/// <summary>Physical-pixel toolbar slots, reserving page identity before optional actions.</summary>
public readonly record struct WebChromeLayout(int ButtonWidth, int NavigationCount, bool ShowHide, int TitleLeft, int TitleRight)
{
    public static WebChromeLayout Fit(int width, double scale, bool navigation, bool companion)
    {
        width = Math.Max(0, width);
        int button = Math.Min(width, Math.Max(1, (int)Math.Ceiling(32 * scale)));
        bool hide = !companion && width >= Math.Ceiling(128 * scale);
        int rightWidth = button * (hide ? 2 : 1);
        int padding = Math.Min((int)Math.Ceiling(8 * scale), Math.Max(0, (width - rightWidth) / 4));
        int identity = Math.Min((int)Math.Ceiling(96 * scale), Math.Max(0, width - rightWidth - 2 * padding));
        int count = !navigation || button == 0 ? 0 : Math.Clamp(Math.Min(
            (width - rightWidth - 2 * padding - identity) / button,
            (width - 2 * padding - identity) / (2 * button)), 0, 5);
        int left = count * button + padding, right = width - rightWidth - padding;
        int symmetric = Math.Max(left, width - right);
        if (width - 2 * symmetric >= identity) { left = symmetric; right = width - symmetric; }
        return new(button, count, hide, left, Math.Max(left, right));
    }
}
