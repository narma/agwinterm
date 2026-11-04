namespace Agwinterm.Pty.Tests;

public class AgtermPageDownloadBudgetTests
{
    [Fact]
    public async Task OversizedChunkIsRefusedBeforeWritingAndClosingDeletesItsFile()
    {
        var budget = new AgtermPageDownloadBudget(resourceBytes: 5, pageBytes: 10);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using (var body = budget.Create(() => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose)))
        {
            await body.WriteAsync(new byte[4]);
            await Assert.ThrowsAsync<IOException>(async () => await body.WriteAsync(new byte[2]));
            Assert.Equal(4, body.Length);
        }
        Assert.False(File.Exists(path));
        using var next = budget.Create(() => new MemoryStream());
        await next.WriteAsync(new byte[5]);
        Assert.Equal(5, next.Length);
    }

    [Fact]
    public void BrowserRetainsPageReservationUntilBodyIsConsumed()
    {
        var budget = new AgtermPageDownloadBudget(resourceBytes: 4, pageBytes: 6);
        using var first = budget.Create(() => new MemoryStream());
        first.Write(new byte[4]); first.Position = 0;
        using var browser = new AgtermPageResponseStream(first, () => { });
        using var second = budget.Create(() => new MemoryStream());
        second.Write(new byte[2]);
        Assert.Throws<IOException>(() => second.Write(new byte[1]));
        Assert.Equal(2, second.Length);
        Assert.Equal(4, browser.Read(new byte[4], 0, 4));
        second.Write(new byte[2]);
        Assert.Equal(4, second.Length);
        // Repeated browser disposal must not release another resource's bytes.
        browser.Dispose(); browser.Dispose();
        using var third = budget.Create(() => new MemoryStream());
        Assert.Throws<IOException>(() => third.Write(new byte[3]));
    }

    [Fact]
    public void EmptyFilesAndFailedCreationCannotEvadeTheOpenResourceLimit()
    {
        var budget = new AgtermPageDownloadBudget(resources: 1);
        Assert.Throws<IOException>(() => budget.Create(() => throw new IOException("Disk unavailable")));
        using var first = budget.Create(() => new MemoryStream());
        bool created = false;
        Assert.Throws<IOException>(() => budget.Create(() => { created = true; return new MemoryStream(); }));
        Assert.False(created);
        first.Dispose();
        using var next = budget.Create(() => new MemoryStream());
        Assert.True(next.CanWrite);
    }

    [Fact]
    public async Task ConcurrentResourcesShareOnePageBudget()
    {
        var budget = new AgtermPageDownloadBudget(resourceBytes: 8, pageBytes: 8);
        using var first = budget.Create(() => new MemoryStream());
        using var second = budget.Create(() => new MemoryStream());
        var results = await Task.WhenAll(new[] { first, second }.Select(body => Task.Run(() =>
        {
            try { body.Write(new byte[5]); return true; }
            catch (IOException) { return false; }
        })));
        Assert.Single(results, success => success);
        Assert.Equal(5, first.Length + second.Length);
    }
}
