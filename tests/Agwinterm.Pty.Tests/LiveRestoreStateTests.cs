using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public partial class RestoreStateTests
{
    [Fact]
    public void PendingRestartPreservesItsExactPredecessorAndRejectsInvalidOrigins()
    {
        var previous = WslLiveIdentityTests.Binding();
        var pending = previous with { BindingId = Guid.NewGuid().ToString(), Identity = null, Pending = true,
            TransportTicket = null, Recovery = "uncertain", RestartOrigin = new(previous.BindingId, previous.Identity!),
            Endpoint = previous.Endpoint with { LedgerDirectory = "/private/ledger", HelperPath = "/private/helper.py" } };
        var saved = JsonSerializer.SerializeToElement(pending);
        Assert.Equal(pending, RestoreState.LoadLiveBinding(saved, out var error));
        Assert.Null(error);
        Assert.False((pending with { RestartOrigin = new(pending.BindingId, previous.Identity!) }).IsValid);
        Assert.False((pending with { RestartOrigin = new("bad", previous.Identity!) }).IsValid);
        Assert.False((previous with { RestartOrigin = pending.RestartOrigin }).IsValid);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Version\":99}")]
    [InlineData("{\"Version\":1,\"Endpoint\":null}")]
    [InlineData("\"corrupt\"")]
    public void InvalidLiveBindingIsPreservedAndSuppressesOrdinaryRestore(string raw)
    {
        var pane = JsonSerializer.Deserialize<PaneState>("{\"Id\":\"p\",\"LiveBinding\":" + raw + "}")!;
        Assert.True(RestoreState.HasLiveBinding(pane.LiveBinding));
        Assert.Null(RestoreState.LoadLiveBinding(pane.LiveBinding, out var error));
        Assert.NotNull(error);
        var reloaded = JsonSerializer.Deserialize<PaneState>(JsonSerializer.Serialize(pane))!;
        Assert.Equal(raw, reloaded.LiveBinding.GetRawText());
    }

    [Fact]
    public void BindingRestoresRecordedDefaultsIndependentlyOfCurrentProfile()
    {
        var original = WslLiveIdentityTests.Binding();
        var pane = new PaneState { Id = "ui", LiveBinding = JsonSerializer.SerializeToElement(original) };
        var reloaded = JsonSerializer.Deserialize<PaneState>(JsonSerializer.Serialize(pane))!;
        var binding = RestoreState.LoadLiveBinding(reloaded.LiveBinding, out var error);
        Assert.Null(error);
        Assert.Equal(original, binding);
        Assert.Equal(new[] { "--distribution", "Ubuntu", "--user", "alice" }, binding!.Endpoint.Selectors);
        Assert.False(RestoreState.HasLiveBinding(new PaneState().LiveBinding));
        Assert.DoesNotContain("LiveBinding", JsonSerializer.Serialize(new PaneState()));
    }

    [Fact]
    public void LiveCheckpointWriteFailureIsObservableAndLeavesBindingIntact()
    {
        string directory = Path.Combine(Path.GetTempPath(), "agwinterm-live-save-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string? failedPath = null, failedReason = null;
        var writer = new StateWriter(TimeSpan.Zero, failed: (path, why) => { failedPath = path; failedReason = why; });
        var binding = WslLiveIdentityTests.Binding();
        try
        {
            Assert.False(writer.Publish(directory, JsonSerializer.Serialize(binding), out var why));
            Assert.NotNull(why);
            Assert.Equal(directory, failedPath);
            Assert.Equal(why, failedReason);
            Assert.True(binding.IsValid);
        }
        finally
        {
            writer.Shutdown(TimeSpan.FromSeconds(1));
            File.Delete(directory + ".tmp");
            Directory.Delete(directory);
        }
    }
}
