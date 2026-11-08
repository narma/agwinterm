namespace Agwinterm.Core;

/// <summary>Immutable selection payloads, independent of native rows and anchors.</summary>
public sealed record SelectionFormats(string Text,string? Vt,string? Html)
{
    public const int MaximumFormatBytes=100000;
    public string? Get(int format) => format switch { 0=>Text,1=>Vt,2=>Html,_=>null };
    public static SelectionFormats Capture(GhosttyTerminalCore core,int ar,int ac,int fr,int fc,bool block,string text)
    {
        if(core.SynchronizedOutput) return new(text,null,null);
        string? Format(int kind)
        {
            try { return core.FormatSelection(ar,ac,fr,fc,block,kind,MaximumFormatBytes); }
            catch(InvalidDataException) { return null; }
        }
        return new(text,Format(1),Format(2));
    }
    public string? FileContent(int format) => format == 2 && Html is { } html
        ? "<!doctype html>\n<html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>Terminal selection</title></head><body>"+html+"</body></html>\n"
        : Get(format);
}
