namespace Agwinterm.Pty.Tests;

public class AgtermPageResponseStreamTests
{
    [Fact]
    public void ConsumingBodyReleasesTemporaryFileWithoutWaitingForBrowserDispose()
    {
        string path = Path.GetTempFileName();
        int released = 0;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
            4096, FileOptions.DeleteOnClose);
        file.Write([1, 2, 3]); file.Position = 0;
        using var response = new AgtermPageResponseStream(file, () => released++);
        var buffer = new byte[2];
        Assert.Equal(2, response.Read(buffer, 0, 2));
        Assert.True(File.Exists(path));
        Assert.Equal(1, response.Read(buffer, 0, 2));
        Assert.False(File.Exists(path));
        Assert.Equal(0, response.Read(buffer, 0, 2));
        response.Dispose();
        Assert.Equal(1, released);
    }

    [Fact]
    public void CancelledDocumentClosesPartiallyReadBodyOnce()
    {
        var content = new MemoryStream(new byte[10]);
        int released = 0;
        using var response = new AgtermPageResponseStream(content, () => released++);
        Assert.Equal(1, response.Read(new byte[1], 0, 1));
        response.Dispose(); response.Dispose();
        Assert.False(content.CanRead);
        Assert.Equal(1, released);
        Assert.Equal(0, response.Read(new byte[1], 0, 1));
    }
}
