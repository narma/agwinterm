using System.Text.Json;
using Agwinterm.Ctl;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AgtermRemoteBindingTests
{
    internal static AgtermRemoteBinding Binding()
    {
        string name = "agterm-11111111111111111111111111111111";
        var process = new { pid = 100, startSeconds = 123UL, startMicros = 45UL,
            boot = "11111111-1111-1111-1111-111111111111", uid = 501 };
        var identity = JsonSerializer.SerializeToElement(new { platform = "macos", daemon = process,
            workload = process with { pid = 101 }, socket = new ulong[] { 1, 2 }, path = "/tmp/origin/" + name, executable = "/app/zmx" });
        return new(1, "agterm-ssh", Guid.NewGuid().ToString(), new("Ubuntu", "alice", "origin", "/home/alice/agent.sock"),
            "/opt/bridge", new("/app/zmx", "/tmp/origin"), "origin-session", "origin-window", "origin-workspace",
            "left", name, identity, new string('a', 64));
    }

    [Fact]
    public void RoundtripKeepsOriginAndResolvedSelectorsWithoutLocalOwnership()
    {
        var binding = Binding();
        Assert.True(binding.IsValid);
        var raw = JsonSerializer.SerializeToElement(binding);
        var loaded = AgtermRemoteBinding.Load(raw, out var error);
        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(binding.Target, loaded.Target);
        Assert.Equal(binding.OriginSession, loaded.OriginSession);
        Assert.Equal("borrowed", loaded.Ownership);
        Assert.True(JsonElement.DeepEquals(binding.Identity, loaded.Identity));
        Assert.True(RestoreState.HasLiveBinding(raw));
        Assert.Null(RestoreState.LoadLiveBinding(raw, out _));
        var args = AgtermRemoteOperations.AttachmentArgs(loaded, "fresh-nonce");
        Assert.Equal(new[] { "--distribution", "Ubuntu", "--user", "alice" }, args.Take(4));
        Assert.Contains("-tt", args);
        Assert.DoesNotContain("zmx attach", string.Join(' ', args));
        string encoded = args[^1].Split(' ')[^1].Trim('\'');
        using var request = JsonDocument.Parse(Convert.FromBase64String(encoded));
        Assert.False(request.RootElement.GetProperty("claim").GetBoolean());
        Assert.Equal("attach", request.RootElement.GetProperty("verb").GetString());
        Assert.Equal(binding.ClientSha256, request.RootElement.GetProperty("clientSha256").GetString());
    }

    [Fact]
    public void InvalidRemoteMetadataAlwaysRetainsReplaySuppression()
    {
        var binding = Binding();
        foreach (var invalid in new[] { binding with { Version = 2 }, binding with { Identity = default },
            binding with { Role = "active" }, binding with { ClientSha256 = "bad" },
            binding with { Target = new("", "alice", "other") }, binding with { Recovery = "fresh-started" } })
        {
            Assert.False(invalid.IsValid);
            if (invalid.Identity.ValueKind == JsonValueKind.Undefined) continue;
            var raw = JsonSerializer.SerializeToElement(invalid);
            Assert.True(RestoreState.HasLiveBinding(raw));
            Assert.Null(AgtermRemoteBinding.Load(raw, out var error));
            Assert.NotNull(error);
        }
        string json = JsonSerializer.Serialize(binding).Replace("\"User\":\"alice\"", "\"User\":\"alice\",\"User\":\"root\"");
        using var doc = JsonDocument.Parse(json);
        Assert.Null(AgtermRemoteBinding.Load(doc.RootElement, out _));
    }

    [Theory]
    [InlineData("terminate")]
    [InlineData("restart")]
    [InlineData("kill")]
    public void RemoteWorkloadMutationIsNeverACommand(string verb) => Assert.Throws<ArgumentException>(() =>
        WslLiveControl.Validate("zmx.remote." + verb, JsonSerializer.SerializeToElement(new { }), "pane"));

    [Theory]
    [InlineData("list", "--distribution", "Ubuntu", "--user", "alice", "--host", "origin")]
    [InlineData("retry", "--target", "pane", "--window", "window")]
    public void RemoteCliVocabularyAcceptsOnlyDeclaredOptions(string verb, params string[] options)
    {
        Assert.True(CtlArguments.TryParse(["zmx", "remote", verb, .. options], out _, out _, out _, out _, out _, out var error), error);
        Assert.False(CtlArguments.TryParse(["zmx", "remote", verb, .. options, "--source", "shell"], out _, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void RemoteSelectorsRejectAmbiguityBeforeTransport()
    {
        using var duplicate = JsonDocument.Parse("{\"distribution\":\"Ubuntu\",\"user\":\"a\",\"host\":\"one\",\"host\":\"two\"}");
        Assert.Throws<ArgumentException>(() => AgtermRemoteControl.Validate("zmx.remote.list", duplicate.RootElement, null));
        Assert.Throws<ArgumentException>(() => AgtermRemoteControl.Validate("zmx.remote.retry", default, "active"));
        var binding = Binding();
        var state = new AppState();
        var pane = new PaneState { Id = "local-pane", LiveBinding = JsonSerializer.SerializeToElement(binding) };
        string serialized = JsonSerializer.Serialize(pane);
        var restored = JsonSerializer.Deserialize<PaneState>(serialized)!;
        Assert.True(RestoreState.HasLiveBinding(restored.LiveBinding));
        Assert.Equal(binding.Target, AgtermRemoteBinding.Load(restored.LiveBinding, out _)!.Target);
    }
}
