namespace Agwinterm.Core;

/// <summary>Remote pages browse the web; local documents retain their resource grant.</summary>
public static class WebNavigationPolicy
{
    public static bool AllowsFrame(string source, string destination, bool local, bool review, bool chromeless)
    {
        if (review || !Uri.TryCreate(destination, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "about") return true;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return false;
        return !local && !chromeless || uri.GetLeftPart(UriPartial.Authority) == new Uri(source).GetLeftPart(UriPartial.Authority);
    }

    public static bool Allows(string source, string destination, bool local, bool review, bool browse = false)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "about") return true;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return false;
        if (!local && browse) return !review;
        if (uri.GetLeftPart(UriPartial.Authority) != new Uri(source).GetLeftPart(UriPartial.Authority)) return false;
        return !review || destination.Split('#')[0] == source.Split('#')[0];
    }
}
