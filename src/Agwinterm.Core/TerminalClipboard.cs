namespace Agwinterm.Core;

public sealed record ClipboardContent(string Mime,byte[] Data);
public sealed record ClipboardRequest(bool Read,int Location,ClipboardContent[] Contents,string[] Mimes,bool List=false)
{
    public const int MaximumBytes=1024*1024, MaximumItems=16;
    public bool IsValid() => Location is >= 0 and <= 2 && Contents is not null && Mimes is not null &&
        Contents.Length <= MaximumItems && Mimes.Length <= MaximumItems &&
        Contents.All(c=>c is not null && ValidMime(c.Mime) && c.Data is not null) &&
        Mimes.All(ValidMime) && Contents.Sum(c=>(long)c.Data.Length) <= MaximumBytes &&
        Contents.Select(c=>c.Mime).Distinct(StringComparer.Ordinal).Count() == Contents.Length;
    public static bool ValidMime(string? mime) => mime is { Length: > 0 and <= 128 } &&
        mime.Contains('/') && mime.All(c=>c is >= '!' and <= '~');
}
// Status uses Ghostty's write results. Read I/O errors are mapped to its read enum at the ABI seam.
public sealed record ClipboardResult(int Status,ClipboardContent[]? Contents=null,string[]? Available=null)
{
    public static readonly ClipboardResult Denied=new(1), Unsupported=new(2), Busy=new(3), Invalid=new(4), IoError=new(5);
}
