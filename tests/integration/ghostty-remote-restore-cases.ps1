# Dot-sourced by restore-roundtrip inside the owned UI VM. This verifies saved
# remote-binding transport adoption, not SSH/origin availability: the transport
# is a real hosted local shell, with structurally valid fixture metadata.
$caseName = 'ghostty-remote-host-adoption'
if (-not $Only -or $caseName -like $Only) {
"-- $caseName --"
$s = $null
try {
    . "$PSScriptRoot/host-probe.ps1"
    $s = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe 'rr-remote-upgrade' -Conf @('session-host = server','emulator-core = ghostty')
    $hostNamespace = $s.Pipe + '-owner-v1'
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'Original hosted shell did not start' }
    $original = @(Invoke-TestHostProbe $exe $hostNamespace list | Where-Object Id -eq $sid)[0]
    Invoke-TestHostProbe $exe $hostNamespace create-abandoned
    if (-not $original.CreationTicket -or $original.ChildPid -le 0) { throw 'Missing exact hosted identity' }
    [void][AgwUi]::PostMessageW($s.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $s.Proc.WaitForExit(20000)) { throw 'Initial UI did not close' }
    $stateFile = StateFile $s
    $state = Get-Content -LiteralPath $stateFile.FullName -Raw | ConvertFrom-Json -AsHashtable
    $savedPane = @($state.Workspaces.Sessions.Panes | Where-Object Id -eq $sid)[0]
    if (-not $savedPane) { throw 'Original pane was not saved' }
    $daemon = 'agterm-11111111111111111111111111111111'
    $identity = @{ platform='macos'; daemon=@{pid=100;startSeconds=123;startMicros=45;boot='11111111-1111-1111-1111-111111111111';uid=501};
        workload=@{pid=101;startSeconds=123;startMicros=45;boot='11111111-1111-1111-1111-111111111111';uid=501};
        socket=@(1,2);path="/tmp/origin/$daemon";executable='/app/zmx' }
    $savedPane.LiveBinding = @{ Version=1;Provider='agterm-ssh';BindingId=[guid]::NewGuid().ToString();
        Target=@{Distribution='Ubuntu';User='alice';Destination='origin.invalid';AgentSocket='/home/alice/agent.sock'};
        Bridge='/opt/bridge';Endpoint=@{executable='/app/zmx';socketDirectory='/tmp/origin'};
        OriginSession='origin-session';OriginWindow='origin-window';OriginWorkspace='origin-workspace';
        Role='left';Daemon=$daemon;Identity=$identity;ClientSha256=('a'*64);Recovery='remote-reattached';
        TransportTicket=$original.CreationTicket;AttachmentNonce='fixture-nonce' }
    $savedPane.Command = 'SHOULD-NOT-REPLAY-REMOTE'
    $savedPane.RestoreCommand = 'SHOULD-NOT-REPLAY-REMOTE'
    $savedPane.Buffer = @('SHOULD-NOT-SEED-REMOTE')
    $state | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $stateFile.FullName -Encoding utf8
    foreach ($hostSetting in 'server-rust','in-process') {
        # Settings for ordinary sessions may change while a remote transport survives.
        "session-host = $hostSetting" | Set-Content (Join-Path $s.AppDir 'agwinterm.conf') -Encoding utf8
        $expectedCore = 'ghostty'
        'emulator-core = rust' | Add-Content (Join-Path $s.AppDir 'agwinterm.conf')
        if (-not (Start-SandboxProcess $s @('--pipe',$s.Pipe,'--app-id',$s.AppId))) { throw 'Restoring UI did not start' }
        Start-Sleep -Seconds 2
        Check "$hostSetting`: requested core is $expectedCore" ((Reply $s @('config','get','emulator-core')).result -eq $expectedCore)
        $node = Node $s $sid
        Check "$hostSetting`: remote binding adopts surviving transport" ($node.liveSessions.$sid.recovery -eq 'host-adopted') ($node | ConvertTo-Json -Depth 8 -Compress)
        Check "$hostSetting`: saved commands and buffer are not replayed" ((PaneText $s $sid) -notlike '*SHOULD-NOT-*')
        if ($hostSetting -eq 'server-rust') { Start-Sleep -Seconds 33 }
        $hosted = @(Invoke-TestHostProbe $exe $hostNamespace list)
        $actual = @($hosted | Where-Object Id -eq $sid)[0]
        Check "$hostSetting`: abandoned transport cleaned without losing restored pane" (@($hosted | Where-Object Id -eq 'abandoned-transport').Count -eq 0)
        Check "$hostSetting`: same child and exact creation ticket, attached" ($actual.Attached -and $actual.ChildPid -eq $original.ChildPid -and $actual.CreationTicket -eq $original.CreationTicket)
        [void][AgwUi]::PostMessageW($s.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $s.Proc.WaitForExit(20000)) { throw 'Adopting UI did not close' }
    }
} catch { Check "$caseName ran to completion" $false $_.Exception.ToString() }
finally {
    if ($s) {
        Get-TestAgentHosts $s | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Stop-Sandbox $s
    }
}
}
