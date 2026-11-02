using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class WslOriginExportTests
{
    private static readonly WslLiveEndpoint Endpoint = new(Guid.NewGuid().ToString(), "Ubuntu", "alice",
        "/opt/zmx", "/run/user/1000/aw", "origin");
    private static readonly WslOriginScope Scope = WslOriginScope.From(Endpoint);
    private const string Adapter = "/home/alice/.local/lib/agwinterm/export/manifest-1/attach";

    private static WslOriginPane Pane(string role = "left")
    {
        var process = new LinuxProcessIncarnation(123, "11111111-1111-1111-1111-111111111111", 42, "pid:[1]");
        var identity = new WslDaemonIdentity("agterm-" + Guid.NewGuid().ToString("N"), process,
            process with { Pid = 124 }, 1, 9);
        string id = Guid.NewGuid().ToString();
        var binding = new WslLiveBinding(1, "wsl-zmx", Guid.NewGuid().ToString(), id,
            Endpoint with { Id = Guid.NewGuid().ToString() }, identity, "owned", "zmx-reattached");
        return new(id, role, binding, new(binding.BindingId, Scope, identity, true), true);
    }

    private static WslOriginSession Session(params WslOriginPane[] panes) => new("session", "Работа 世界",
        "window", "Window", "workspace", "Workspace", "/home/alice/a b", panes.Length == 2 ? "vertical" : null, panes);

    [Fact]
    public void PerBindingEndpointIdsDoNotSplitTheExecutionNamespace()
    {
        var pane = Pane();
        Assert.NotEqual(Endpoint.Id, pane.Binding!.Endpoint.Id);
        Assert.True(WslOriginExport.Eligible(Scope, pane));
    }

    [Theory]
    [InlineData("external")]
    [InlineData("pending")]
    [InlineData("missing")]
    [InlineData("uncertain")]
    [InlineData("unavailable")]
    [InlineData("unsupported")]
    [InlineData("closing")]
    [InlineData("detached")]
    [InlineData("ordinary")]
    [InlineData("wrong-pane")]
    [InlineData("unobserved")]
    [InlineData("not-ready")]
    [InlineData("wrong-binding")]
    public void UnprovenOrBorrowedPaneIsExcluded(string condition)
    {
        var pane = Pane();
        pane = condition switch
        {
            "external" => pane with { Binding = pane.Binding! with { Ownership = "external" } },
            "pending" => pane with { Binding = pane.Binding! with { Pending = true } },
            "closing" => pane with { Closing = true },
            "detached" => pane with { Attached = false },
            "ordinary" => pane with { Binding = null },
            "wrong-pane" => pane with { Id = "other" },
            "unobserved" => pane with { Observation = null },
            "not-ready" => pane with { Observation = pane.Observation! with { Ready = false } },
            "wrong-binding" => pane with { Observation = pane.Observation! with { BindingId = Guid.NewGuid().ToString() } },
            _ => pane with { Binding = pane.Binding! with { Recovery = condition } }
        };
        Assert.False(WslOriginExport.Eligible(Scope, pane));
    }

    [Theory]
    [InlineData("distribution")]
    [InlineData("user")]
    [InlineData("origin")]
    [InlineData("executable")]
    [InlineData("socket")]
    public void EveryScopeComponentIsRequiredOnBindingAndObservation(string component)
    {
        var other = component switch
        {
            "distribution" => Scope with { Distribution = "Debian" },
            "user" => Scope with { User = "bob" },
            "origin" => Scope with { OriginId = "other" },
            "executable" => Scope with { Executable = "/opt/other" },
            _ => Scope with { SocketDirectory = "/run/user/1000/other" }
        };
        var pane = Pane();
        Assert.False(WslOriginExport.Eligible(other, pane));
        Assert.False(WslOriginExport.Eligible(Scope, pane with { Observation = pane.Observation! with { Scope = other } }));
        // Even a matching observation in the requested namespace cannot bless a foreign binding.
        Assert.False(WslOriginExport.Eligible(other, pane with { Observation = pane.Observation! with { Scope = other } }));
    }

    [Theory]
    [InlineData("daemon")]
    [InlineData("workload")]
    [InlineData("boot")]
    [InlineData("namespace")]
    [InlineData("inode")]
    [InlineData("device")]
    [InlineData("name")]
    public void ReplacedIncarnationNeverSatisfiesObservation(string component)
    {
        var pane = Pane();
        var identity = pane.Observation!.Identity;
        identity = component switch
        {
            "daemon" => identity with { Daemon = identity.Daemon with { StartTicks = 99 } },
            "workload" => identity with { Workload = identity.Workload with { Pid = 999 } },
            "boot" => identity with { Daemon = identity.Daemon with { BootId = Guid.NewGuid().ToString() } },
            "namespace" => identity with { Daemon = identity.Daemon with { PidNamespace = "pid:[2]" } },
            "inode" => identity with { SocketInode = 100 },
            "device" => identity with { SocketDevice = 100 },
            _ => identity with { Name = "agterm-" + Guid.NewGuid().ToString("N") }
        };
        Assert.False(WslOriginExport.Eligible(Scope, pane with { Observation = pane.Observation with { Identity = identity } }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("vertical")]
    [InlineData("horizontal")]
    public void ProjectionRoundtripsActualEnvelopeAndDoesNotAdvertisePresentation(string? axis)
    {
        var left = Pane();
        var right = Pane("right");
        var session = axis is null ? Session(left) : Session(right, left) with { SplitAxis = axis };
        var json = WslOriginExport.Project(Scope, Adapter, [session]);
        var tree = AgtermRemoteDiscovery.Decode(0, json);
        Assert.Equal(new AgtermEndpoint(Adapter, Scope.SocketDirectory), tree.Endpoint);
        var row = Assert.Single(tree.Sessions).Session;
        Assert.Equal(session.Id, row.Id);
        Assert.Equal(session.Name, row.Name);
        Assert.Equal(session.WindowId, row.WindowId);
        Assert.Equal(session.WorkspaceId, row.WorkspaceId);
        Assert.Equal(session.Cwd, row.Cwd);
        Assert.Equal(axis, row.SplitAxis);
        Assert.Equal(left.Binding!.Identity!.Name, row.Panes[0].Daemon);
        if (axis is not null) Assert.Equal(right.Binding!.Identity!.Name, row.Panes[1].Daemon);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("result").GetProperty("remote").TryGetProperty("presentation", out _));
    }

    [Fact]
    public void PartialSplitIsOmittedAndSwapUsesCurrentRole()
    {
        var left = Pane();
        var right = Pane("right");
        var absent = right with { Observation = null };
        Assert.Empty(AgtermRemoteDiscovery.Decode(0, WslOriginExport.Project(Scope, Adapter, [Session(left, absent)])).Sessions);
        var swapped = Session(left with { Role = "right" }, right with { Role = "left" });
        var row = Assert.Single(AgtermRemoteDiscovery.Decode(0, WslOriginExport.Project(Scope, Adapter, [swapped])).Sessions).Session;
        Assert.Equal(right.Binding!.Identity!.Name, row.Panes[0].Daemon);
        Assert.Equal(left.Binding!.Identity!.Name, row.Panes[1].Daemon);
    }

    [Fact]
    public void AmbiguousTopologyAndIdentityAreRefused()
    {
        var left = Pane();
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, Adapter, [Session(left, Pane())]));
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, Adapter, [Session(Pane("right"))]));
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, Adapter, [Session(left), Session(left)]));
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, Adapter, [Session(left), Session(left) with { Id = "other" }]));
        var duplicate = Pane("right");
        duplicate = duplicate with
        {
            Binding = duplicate.Binding! with { Identity = left.Binding!.Identity },
            Observation = duplicate.Observation! with { Identity = left.Binding!.Identity! }
        };
        Assert.Throws<AgtermDiscoveryException>(() => WslOriginExport.Project(Scope, Adapter, [Session(left, duplicate)]));
        Assert.Throws<AgtermDiscoveryException>(() => WslOriginExport.Project(Scope, Adapter,
            [Session(left), Session(Pane()) with { Id = "other", WindowName = "conflicting" }]));
    }

    [Fact]
    public void RawZmxAndOversizedSnapshotsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, Scope.Executable, []));
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, "C:\\zmx.exe", []));
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, Adapter,
            Enumerable.Repeat(Session(Pane()), AgtermRemoteDiscovery.MaxSessions + 1).ToArray()));
        var rows = Enumerable.Range(0, 1024).Select(i => Session(Pane()) with
        {
            Id = "s" + i, Name = new string('x', 4096)
        }).ToArray();
        Assert.Throws<ArgumentException>(() => WslOriginExport.Project(Scope, Adapter, rows));
    }

    [Fact]
    public async Task DiscoveryProbesOnlyEligibleOwnedPanesAndNeverTrustsSavedObservations()
    {
        var owned = Pane();
        var borrowed = Pane();
        borrowed = borrowed with { Binding = borrowed.Binding! with { Ownership = "external" } };
        IReadOnlyList<WslOriginSession> snapshot = [Session(owned), Session(borrowed) with { Id = "borrowed" }];
        int probes = 0;
        string json = await WslOriginDiscovery.DiscoverAsync(new(Scope, Adapter), () => snapshot, (binding, _) =>
        {
            probes++;
            Assert.Equal(owned.Binding, binding);
            return Task.FromResult(owned.Observation!);
        });
        Assert.Equal(1, probes);
        Assert.Single(AgtermRemoteDiscovery.Decode(0, json).Sessions);
        // Despite a ready observation already in the snapshot, fresh probe failure omits the row.
        json = await WslOriginDiscovery.DiscoverAsync(new(Scope, Adapter), () => snapshot,
            (_, _) => throw new WslRecoveryException("missing", "gone"));
        Assert.Empty(AgtermRemoteDiscovery.Decode(0, json).Sessions);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("replace")]
    [InlineData("rename")]
    [InlineData("window")]
    public async Task DiscoveryRejectsConcurrentOriginChanges(string change)
    {
        var pane = Pane();
        var session = Session(pane);
        IReadOnlyList<WslOriginSession> snapshot = [session];
        await Assert.ThrowsAsync<InvalidOperationException>(() => WslOriginDiscovery.DiscoverAsync(new(Scope, Adapter),
            () => snapshot, (_, _) =>
            {
                snapshot = change switch
                {
                    "close" => [],
                    "replace" => [Session(Pane())],
                    "rename" => [session with { Name = "changed" }],
                    _ => [session with { WindowId = "other-window" }]
                };
                return Task.FromResult(pane.Observation!);
            }));
    }

    [Fact]
    public async Task CancellationDoesNotPublishOrRetryAProbe()
    {
        var pane = Pane();
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        var pending = new TaskCompletionSource<WslOriginObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = WslOriginDiscovery.DiscoverAsync(new(Scope, Adapter), () => [Session(pane)], (_, _) =>
        {
            calls++;
            cancel.Cancel();
            return pending.Task;
        }, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        Assert.Equal(1, calls);
        pending.SetResult(pane.Observation!);
        Assert.True(discovery.IsCanceled);
    }

    [Fact]
    public async Task FreshCaptureArraysDoNotCountAsTopologyChanges()
    {
        var pane = Pane();
        string result = await WslOriginDiscovery.DiscoverAsync(new(Scope, Adapter),
            () => [Session(pane with { Observation = null })], (_, _) => Task.FromResult(pane.Observation!));
        Assert.Single(AgtermRemoteDiscovery.Decode(0, result).Sessions);
    }

    [Theory]
    [InlineData("{\"distribution\":\"Ubuntu\"}")]
    [InlineData("[]")]
    [InlineData("{\"distribution\":\"Ubuntu\",\"distribution\":\"Debian\"}")]
    [InlineData("{\"unknown\":\"x\"}")]
    public void IncompleteOrAmbiguousExportRequestIsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => WslLiveControl.Validate("zmx.export.tree", document.RootElement, null));
    }
}
