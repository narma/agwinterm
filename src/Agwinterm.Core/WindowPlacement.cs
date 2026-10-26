namespace Agwinterm.Core;

public readonly record struct DesktopRect(int X, int Y, int Width, int Height);

public static class WindowPlacement
{
    public static DesktopRect Fit(DesktopRect window, DesktopRect work)
    {
        if (work.Width <= 0 || work.Height <= 0) throw new ArgumentException("empty monitor work area");
        int width = Math.Clamp(window.Width, 1, work.Width), height = Math.Clamp(window.Height, 1, work.Height);
        return new(Math.Clamp(window.X, work.X, work.X + work.Width - width),
            Math.Clamp(window.Y, work.Y, work.Y + work.Height - height), width, height);
    }
}
