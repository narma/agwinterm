using System.Text.Json;

namespace Agwinterm.Pty.Tests;

public class AgtermPageBridgeTests
{
    [Theory]
    [InlineData("session.text", "{\"lines\":20}")]
    [InlineData("session.output", "{}")]
    [InlineData("session.type", "{\"text\":\"hello\"}")]
    [InlineData("session.write", "{\"text\":\"hello\"}")]
    [InlineData("session.status", "{\"status\":\"blocked\"}")]
    [InlineData("session.overlay.hide", "{}")]
    [InlineData("session.overlay.show", "{}")]
    [InlineData("session.overlay.toggle", "{}")]
    [InlineData("session.overlay.resize", "{\"size-percent\":60}")]
    [InlineData("session.overlay.reload", "{\"current\":true}")]
    [InlineData("session.overlay.navigate", "{\"direction\":\"back\"}")]
    public void GeneralAllowsScopedCommandsButReviewRefuses(string command, string args)
    {
        string json = JsonSerializer.Serialize(new { cmd = command, args = JsonDocument.Parse(args).RootElement });
        Assert.Equal(command, AgtermPageBridge.Parse(json, false).Command);
        Assert.Throws<IOException>(() => AgtermPageBridge.Parse(json, true));
    }

    [Theory]
    [InlineData("{\"cmd\":\"session.overlay.close\"}")]
    [InlineData("{\"cmd\":\"session.overlay.submit\",\"args\":{\"value\":\"\"}}")]
    public void ReviewRetainsCloseAndEmptySubmission(string json)
    { Assert.Equal(AgtermPageBridge.Parse(json, false).Command, AgtermPageBridge.Parse(json, true).Command); }

    [Theory]
    [InlineData("{\"cmd\":\"session.type\",\"target\":\"active\"}")]
    [InlineData("{\"cmd\":\"session.type\",\"args\":{\"targets\":[\"other\"]}}")]
    [InlineData("{\"cmd\":\"session.type\",\"args\":{\"window\":\"other\"}}")]
    [InlineData("{\"cmd\":\"session.type\",\"args\":{\"pane\":\"right\"}}")]
    [InlineData("{\"cmd\":\"session.type\",\"args\":{\"text\":\"a\",\"text\":\"b\"}}")]
    [InlineData("{\"cmd\":\"session.new\"}")]
    [InlineData("{\"cmd\":\"session.close\"}")]
    [InlineData("{\"cmd\":\"config.set\"}")]
    [InlineData("{\"cmd\":\"batch\"}")]
    [InlineData("{\"cmd\":\"session.overlay\",\"args\":{\"action\":\"open\"}}")]
    [InlineData("{\"cmd\":\"session.overlay.navigate\",\"args\":{\"direction\":\"finder\"}}")]
    public void RefusesRoutingAndCommandsOutsideTheGrant(string json)
    { Assert.Throws<IOException>(() => AgtermPageBridge.Parse(json, false)); }

    [Fact]
    public async Task ResourceNamespacesCannotCrossPages()
    {
        string root = Path.Combine(Path.GetTempPath(), "aw-page-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "отчёт %.html"), "authorized");
            var source = LocalWebSource.Open("отчёт %.html", root, null, root);
            var first = new AgtermPageFiles(Guid.NewGuid().ToString(), source);
            var second = new AgtermPageFiles(Guid.NewGuid().ToString(), source);
            var resource = await first.Open(first.Entry, CancellationToken.None);
            using var reader = new StreamReader(resource.Body);
            Assert.Equal("authorized", await reader.ReadToEndAsync());
            await Assert.ThrowsAsync<IOException>(() => first.Open(second.Entry, CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(() => first.Open(source.ExternalPath, CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.Open(first.Root + "/../secret", CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
