# .NET 11 ReadyToRun startup measurements

Date: 2026-11-02. Worktree: codex/net11-performance, base ceba865 (uncommitted changes).

Both Release win-x64 folder publications are self-contained, built using SDK
11.0.100-rc.3.26522.148, with runtime-async=on. Only PublishReadyToRun differs.
The .NET 11 configuration now defaults PublishReadyToRun to true; an explicit
-p:PublishReadyToRun=false remains available for comparisons. .NET 10 is unchanged.

## Results

| Metric | R2R off | R2R on |
|---|---:|---:|
| Median terminal ready | 590.63 ms | 459.37 ms |
| p95 terminal ready | 798.82 ms | 547.12 ms |
| First sample | 901.11 ms | 988.95 ms |
| Published folder bytes | 98,550,648 | 106,557,560 |
| Application DLL bytes | 1,171,968 | 2,662,400 |

Median improved 22.2% (131.26 ms); folder size increased 8.1%.
The first sample did not improve. These results do not establish a cold-start gain.

## Protocol and limitations

30 sequential launches per variant in separate background Windows Sandbox guests
on the same host (Ryzen 9 7950X, Windows build 26100). Fixed cmd.exe echo marker,
private profile, no session restore, managed emulator, in-process terminal host.
Timing starts before process creation and ends when session.text returns the
marker. This measures terminal readiness including control roundtrip, not first
paint. Samples include the first launch; subsequent launches reuse OS caches.
Variants were measured in blocks, not randomized/interleaved; VM and host load
can affect the comparison. Portable extraction, restored sessions, Rust emulator
and production session-host startup are not covered.

Raw reports are in artifacts/startup-r2r-false.json and
artifacts/startup-r2r-true.json. Their sdk field records the host routing SDK
10.0.303, not the publishing SDK. The guest path contains net10.0-windows because
it is a fixed staging path; both payload runtimeconfig files confirm net11.0 and
runtime 11.0.0-rc.3.26522.148. Publish logs are artifacts/publish-false.log and
artifacts/publish-true.log. Do not infer the tested runtime from the staging path.

## Reproduction

Use the pinned RC3 SDK, then publish the app twice:

```powershell
dotnet publish src/Agwinterm.Win32/Agwinterm.Win32.csproj -c Release -r win-x64 --self-contained true -p:AgwintermNet11=true -p:PublishReadyToRun=false -o artifacts/r2r-false
dotnet publish src/Agwinterm.Win32/Agwinterm.Win32.csproj -c Release -r win-x64 --self-contained true -p:AgwintermNet11=true -p:PublishReadyToRun=true -o artifacts/r2r-true
```

Publish the control client self-contained too. Run sandbox-ui.ps1 with
-Test WebStartupBaseline -Background, the appropriate -Exe, -CtlExe and
-TestParameters @{Runs=30; Output='<absolute report path>'}. Stop only the
created Sandbox instance by its reported ID between variants.

Both publication commands and both 30-run Sandbox tests passed. All 90 host-free
sandbox-ui-routing checks passed. Release CI and portable packaging were not run
as part of this comparison.

## .NET 10 baseline (same protocol)

Added a 30-launch run of the current default configuration using SDK 10.0.303,
self-contained runtime 10.0.11, without ReadyToRun or runtime-async. Application
source and harness match the .NET 11 runs. The same ctl11 payload was staged for
routing consistency; the measured harness talks directly to the named pipe and
does not launch that CLI in its timed path.

| Metric | .NET 10 default | .NET 11, R2R off | .NET 11, R2R on |
|---|---:|---:|---:|
| Median terminal ready | 538.65 ms | 590.63 ms | 459.37 ms |
| p95 terminal ready | 624.55 ms | 798.82 ms | 547.12 ms |
| First sample | 1074.32 ms | 901.11 ms | 988.95 ms |
| Published folder bytes | 92,041,516 | 98,550,648 | 106,557,560 |

.NET 11 with R2R was 14.7% faster in median readiness than the current .NET 10
configuration (79.29 ms). .NET 11 without R2R was 9.6% slower in this comparison.
These are separate sequential batches, so they are observations rather than an
isolated estimate of runtime-version effects. No cold-start or runtime-async
benefit is established by these measurements. All 30 .NET 10 launches passed.
Raw samples: artifacts/startup-net10.json. Build log: artifacts/publish-net10.log.

## Factor comparison: ReadyToRun and application runtime-async

Three additional 30-launch Sandbox batches completed successfully, using the
same harness and source: .NET 10 with R2R; .NET 11 with UseRuntimeAsync=false
(runtime-async=off), both without and with R2R. The latter switch applies to
application projects and project references, not precompiled framework libraries.
No runtime-wide environment override was used. All test-created guests were stopped.

| Runtime | R2R | Application runtime-async | Median ms | p95 ms | First sample ms |
|---|---|---|---:|---:|---:|
| .NET 10 | off | default | 538.65 | 624.55 | 1074.32 |
| .NET 10 | on | default | 464.34 | 548.74 | 962.19 |
| .NET 11 RC3 | off | off | 524.25 | 618.90 | 946.90 |
| .NET 11 RC3 | off | on | 590.63 | 798.82 | 901.11 |
| .NET 11 RC3 | on | off | 464.46 | 539.82 | 964.26 |
| .NET 11 RC3 | on | on | 459.37 | 547.12 | 988.95 |

R2R improves .NET 10 median readiness by 13.8% (74.31 ms). With R2R on and
application runtime-async off, .NET 10 and .NET 11 are effectively equal in this
measurement (464.34 vs 464.46 ms). Turning runtime-async on with R2R changes the
.NET 11 median by only 5.09 ms (1.1%), insufficient to claim a reliable gain from
these separate batches. Without R2R the runtime-async-on batch was slower
(590.63 vs 524.25 ms); this should not be generalized to steady-state throughput.
.NET 11 without either option was 2.7% faster than .NET 10 without R2R, also a
small difference vulnerable to batch noise.

Conclusion: these startup measurements attribute the substantial observed gain
to R2R, not to upgrading the runtime. They do not establish a significant startup
benefit from .NET 11 or runtime-async. The earlier 14.7% comparison bundled both
the runtime upgrade and enabling R2R. R2R for .NET 10 merits consideration; it
has not been enabled by default as part of these measurement-only additions.

New raw reports: artifacts/startup-net10-r2r.json,
artifacts/startup-net11-async-off-r2r-false.json, and
artifacts/startup-net11-async-off-r2r-true.json. Corresponding publish logs use
artifacts/publish-<variant>.log. Folder sizes: 99,866,668; 98,625,800; 106,627,080
bytes respectively. To reproduce, publish .NET 10 with -p:PublishReadyToRun=true;
for .NET 11 add -p:UseRuntimeAsync=false to the previous publish commands.

The six batches were not randomized or interleaved; only the three new variants
were measured in this follow-up. Cold-start, first paint, production Rust/session
host behavior and steady-state async performance remain outside this protocol.

## Release policy

Installer, portable and standalone CLI publications explicitly enable ReadyToRun.
The default release stays on .NET 10. The additional .NET 11 RC3 release enables
runtime-async=on and ReadyToRun, with a separate agwinterm-net11- asset prefix.
The .NET 11 upload waits for the primary release job to avoid release-creation races.

