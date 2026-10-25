namespace Agwinterm.Core;

/// <summary>Requested session-overlay dimensions, independent of a particular window or renderer.</summary>
public readonly record struct OverlayGeometry(int Percent = 0, int? WidthDip = null, int? HeightDip = null)
{
    public bool Floating => Percent != 0 || WidthDip is not null || HeightDip is not null;

    public OverlayGeometry Resize(int? percent, int? width, int? height, bool full)
    {
        if (full) return new();
        var basis = percent is { } p ? new OverlayGeometry(p) : this;
        return basis with { WidthDip = width ?? basis.WidthDip, HeightDip = height ?? basis.HeightDip };
    }

    public (float x, float y, float w, float h) Fit(float x, float y, float width, float height, float dpi)
    {
        width = Math.Max(0, width); height = Math.Max(0, height);
        float factor = Percent == 0 ? 1 : Percent / 100f;
        float w = Math.Clamp(WidthDip is { } wd ? wd * dpi / 96f : width * factor, 0, width);
        float h = Math.Clamp(HeightDip is { } hd ? hd * dpi / 96f : height * factor, 0, height);
        return (x + (width - w) / 2, y + (height - h) / 2, w, h);
    }
}
