namespace Agwinterm.Pty;

/// <summary>A per-page logical path namespace; transport never exports a viewer-local file grant.</summary>
public sealed class AgtermPageFiles
{
    private readonly LocalWebSource _source;
    public string Root { get; }
    public string Entry => Root + "/" + _source.EntryName;
    public AgtermPageFiles(string page, LocalWebSource source)
    {
        if (!Guid.TryParseExact(page, "D", out _)) throw new ArgumentException("Invalid page ID");
        Root = "/agwinterm-pages/" + page;
        _source = source;
    }
    public async Task<AgtermPageResource> Open(string path, CancellationToken cancellation)
    {
        if (!path.StartsWith(Root + "/", StringComparison.Ordinal)) throw new IOException("Wrong page resource grant");
        string relative = path[(Root.Length + 1)..];
        string encoded = "/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        var stream = await _source.OpenReadAsync(encoded, cancellation).ConfigureAwait(false);
        return new(stream, LocalWebSource.ContentType(relative));
    }
}
