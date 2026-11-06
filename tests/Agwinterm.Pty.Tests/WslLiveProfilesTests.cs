using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public partial class WslIntegrationTests
{
    [Fact]
    public void LiveAttachmentUsesNativePayloadAndPreservesStructuredRequest()
    {
        var location = new WslLiveClient.Location("My Ubuntu", "alice", "/tmp", "/home/alice/ledger",
            "/home/alice/helper dir/agwinterm-hook", "/tmp/sockets");
        var args = WslLiveClient.AttachmentArgs(location, new { verb = "attach", text = "quoted ' \" $HOME 😀" });
        Assert.Equal(new[] { "--distribution", "My Ubuntu", "--user", "alice", "--exec", location.Helper,
            "--zmx", "--request" }, args[..^1]);
        using var request = JsonDocument.Parse(Convert.FromBase64String(args[^1]));
        Assert.Equal("quoted ' \" $HOME 😀", request.RootElement.GetProperty("text").GetString());
        Assert.Throws<InvalidOperationException>(() => WslLiveClient.AttachmentArgs(
            location with { Helper = "/home/alice/old/zmx_helper.py" }, new { verb = "attach" }));
        Assert.DoesNotContain(typeof(WslLiveClient).Assembly.GetManifestResourceNames(),
            name => name.StartsWith("Agwinterm.Pty.WslLive.") && name.EndsWith(".py"));
    }

    [Fact]
    public void LiveProfileDefaultsAndArgvStaySeparate()
    {
        var profile = new ShellProfile { Command = "wsl.exe", Zmx = new(true, "/opt/test ' zmx"),
            Args = ["-d", "My Ubuntu", "--user=alice", "--exec", "bash", "-c", "printf '%s' \"$VALUE\""] };
        Assert.Null(WslLiveProfiles.Validate(profile, "server-rust", WslLiveSurface.Ordinary));
        Assert.Null(WslLiveProfiles.Validate(profile, "server", WslLiveSurface.Ordinary));
        Assert.Equal(new[] { "bash", "-c", "printf '%s' \"$VALUE\"" }, WslLiveProfiles.WorkloadArgv(profile));
        Assert.Equal(new[] { "--distribution", "My Ubuntu", "--user", "alice" }, WslLiveProfiles.ResolvedSelectors("My Ubuntu", "alice"));
        profile.Args = null;
        Assert.Equal("/bin/sh", WslLiveProfiles.WorkloadArgv(profile)[0]);
        Assert.Throws<ArgumentException>(() => WslLiveProfiles.ResolvedSelectors("", ""));
    }

    [Theory]
    [InlineData(WslLiveSurface.Quick)]
    [InlineData(WslLiveSurface.Scratch)]
    [InlineData(WslLiveSurface.Program)]
    [InlineData(WslLiveSurface.Handoff)]
    public void ExplicitLiveRequestRefusesTransientSurfaces(WslLiveSurface surface)
    {
        var profile = new ShellProfile { Command = "wsl.exe", Zmx = new(true, "/opt/zmx") };
        Assert.NotNull(WslLiveProfiles.Validate(profile, "server-rust", surface));
        profile.Zmx = new(false);
        Assert.Null(WslLiveProfiles.Validate(profile, "in-process", surface));
    }

    [Theory]
    [InlineData("cmd.exe", "server-rust", "/opt/zmx")]
    [InlineData("wsl.exe", "in-process", "/opt/zmx")]
    [InlineData("wsl.exe", "server-rust", "relative/zmx")]
    [InlineData("wsl.exe", "server-rust", "C:\\zmx.exe")]
    public void InvalidLiveConfigurationRefuses(string command, string host, string executable)
        => Assert.NotNull(WslLiveProfiles.Validate(new() { Command = command, Zmx = new(true, executable) }, host, WslLiveSurface.Ordinary));

    [Theory]
    [InlineData("true")]
    [InlineData("{\"enabled\":\"yes\"}")]
    [InlineData("{\"enabled\":true,\"enabled\":false}")]
    [InlineData("{\"unknown\":false}")]
    public void MalformedZmxOptionDoesNotTurnIntoDisabledProfile(string json)
    {
        var value = JsonSerializer.Deserialize<WslZmxConfiguration>(json)!;
        Assert.True(value.Enabled);
        Assert.NotNull(value.Error);
        Assert.NotNull(JsonSerializer.Deserialize<WslZmxConfiguration>(JsonSerializer.Serialize(value))!.Error);
    }

    [Fact]
    public void LiveWorkloadEnvironmentClearsInheritedSelectionAndKeepsOrigin()
    {
        var input = new Dictionary<string, string> { ["ZMX_SESSION"] = "foreign", ["ZMX_MANAGED"] = "stale",
            ["ZMX_SESSION_PREFIX"] = "foreign", ["ZMX_DIR"] = "/foreign", ["WSLENV"] = "ZMX_SESSION",
            ["AGWINTERM_PANE_ID"] = "origin", ["VALUE"] = "quoted ' value" };
        var filtered = WslLiveProfiles.WorkloadEnvironment(input);
        Assert.Equal(2, filtered.Count);
        Assert.Equal("origin", filtered["AGWINTERM_PANE_ID"]);
        Assert.Equal("quoted ' value", filtered["VALUE"]);
        Assert.Equal("foreign", input["ZMX_SESSION"]);
        var disabled = new ShellProfile { Command = "wsl.exe", Args = ["--exec", "bash", "-i"] };
        Assert.Null(WslLiveProfiles.Validate(disabled, "in-process", WslLiveSurface.Ordinary));
        Assert.Equal(disabled.Args, WslIntegration.LaunchArgs(disabled.Args, null));
    }
}

public class WslLiveIdentityTests
{
    internal static WslLiveBinding Binding()
    {
        var process = new LinuxProcessIncarnation(42, Guid.NewGuid().ToString(), 123, "pid:[4026532257]");
        return new(1, "wsl-zmx", Guid.NewGuid().ToString(), "opaque-origin-pane",
            new(Guid.NewGuid().ToString(), "Ubuntu", "alice", "/opt/zmx", "/run/user/1000/aw", "origin"),
            new("agterm-" + Guid.NewGuid().ToString("N"), process, process with { Pid = 43 }, 1, 100),
            "owned", "host-adopted", "windows-ticket");
    }

    [Fact]
    public void EndpointsAndIncarnationsDoNotCollapseToNamesOrUiIds()
    {
        var a = Binding();
        Assert.True(a.IsValid);
        var b = a with { Endpoint = a.Endpoint with { Distribution = "Other", User = "bob" } };
        Assert.NotEqual(a, b);
        Assert.Equal(a.Identity!.Name, b.Identity!.Name);
        Assert.NotEqual(a.BindingId.Replace("-", ""), a.Identity.Name[7..]);
        Assert.NotEqual(a.Identity, a.Identity with { Daemon = a.Identity.Daemon with { StartTicks = 124 } });
        Assert.True(a.SuppressesReplay);
        Assert.True((a with { Ownership = "external" }).IsValid);
        Assert.False((a with { Ownership = "unknown" }).IsValid);
    }

    [Fact]
    public void MalformedIdentityAndWindowsSocketPathsRefuse()
    {
        var binding = Binding();
        Assert.False((binding with { Endpoint = binding.Endpoint with { SocketDirectory = "/mnt/c/sockets" } }).IsValid);
        Assert.False((binding with { Version = 2 }).IsValid);
        Assert.False((binding with { Identity = binding.Identity! with { Name = "ui-pane" } }).IsValid);
        Assert.False((binding.Identity!.Daemon with { PidNamespace = null! }).IsValid);
        Assert.False((binding with { Identity = null! }).IsValid);
    }
}
