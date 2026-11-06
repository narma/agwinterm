namespace Agwinterm.Core;

/// <summary>
/// Native endpoint tracking plus immutable copy text. A tracked coordinate may
/// be clamped when a partial scroll deletes its content; validate the selected
/// text after mutations so such a coordinate cannot silently select other text.
/// </summary>
public sealed class GhosttySelection : IDisposable
{
    private readonly GhosttyTerminalCore _core;
    private readonly GhosttyAnchor _anchor, _focus;
    private readonly bool _block, _alt;
    private long _version;
    private bool _valid = true;
    private readonly int _maximumCharacters;
    public string Text { get; }
    internal GhosttySelection(GhosttyTerminalCore core, int ar, int ac, int fr, int fc, bool block,int maximumCharacters)
    {
        _core = core; _block = block; _alt = core.IsAltScreen; _version = core.TextVersion;
        _maximumCharacters=maximumCharacters;
        Text = TerminalSelectionText.Read(core, ar, ac, fr, fc, block,maximumCharacters);
        _anchor = core.TrackPoint(ar, ac);
        try { _focus = core.TrackPoint(fr, fc); }
        catch { _anchor.Dispose(); throw; }
    }
    public bool TryLocate(out int ar, out int ac, out int fr, out int fc)
    {
        ar = ac = fr = fc = 0;
        if (!_valid || _alt != _core.IsAltScreen || !_anchor.TryLocate(out ar, out ac) || !_focus.TryLocate(out fr, out fc))
            return _valid = false;
        if (_version != _core.TextVersion)
        {
            _version = _core.TextVersion;
            try { _valid = Text == TerminalSelectionText.Read(_core, ar, ac, fr, fc, _block,_maximumCharacters); }
            catch(InvalidDataException) { _valid=false; }
        }
        return _valid;
    }
    public void Dispose() { _anchor.Dispose(); _focus.Dispose(); }
}
