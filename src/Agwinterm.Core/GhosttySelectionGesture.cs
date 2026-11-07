namespace Agwinterm.Core;

/// <summary>A tracked press origin. Each update evaluates press/drag atomically in Ghostty,
/// so local input and asynchronous hosted requests use the same selection rules.</summary>
public sealed class GhosttySelectionGesture : IDisposable
{
    private readonly GhosttyTerminalCore _core;
    private readonly GhosttyAnchor _origin;
    private readonly bool _alt;
    private readonly int _behavior;
    private readonly string _delimiters;
    public GhosttySelectionGesture(GhosttyTerminalCore core,int row,int column,int behavior,string delimiters)
    {
        _core=core; _alt=core.IsAltScreen; _behavior=behavior; _delimiters=delimiters;
        _origin=core.TrackPoint(row,column);
    }
    public bool Update(int row,int column,bool block,out int ar,out int ac,out int fr,out int fc)
    {
        ar=ac=fr=fc=0;
        return _core.IsAltScreen == _alt && _origin.TryLocate(out int y,out int x) &&
            _core.SelectionDrag(y,x,row,column,_behavior,block,_delimiters,out ar,out ac,out fr,out fc);
    }
    public void Dispose() => _origin.Dispose();
}
