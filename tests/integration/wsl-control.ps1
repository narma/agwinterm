param([string]$Distro = 'Ubuntu', [switch]$NativeHooks, [string]$LinuxUser = '', [switch]$NoPython, [switch]$Portable)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
if ($Portable) {
    $exe = (Get-ChildItem (Join-Path $repo 'installer/Output/agwinterm-portable-*-win-x64.exe') |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $exe) { throw 'Build the portable executable first' }
    $ctl = Join-Path $repo 'installer/stage-portable/tools/agwintermctl.exe'
    $NativeHooks = $true
}
$id = 'agwinterm-wsl-test-' + [Guid]::NewGuid().ToString('N')
$state = Join-Path $env:LOCALAPPDATA $id
$linuxHome = '/tmp/' + $id
$selectors = @('-d', $Distro)
if ($LinuxUser) { $selectors += @('-u', $LinuxUser) }
$process = $null
function Ctl([string[]]$Arguments) {
    $raw = (& $ctl @Arguments --pipe $id --json) -join "`n"
    try { $reply = $raw | ConvertFrom-Json } catch { throw "CLI $Arguments returned: $raw" }
    if (-not $reply.ok) { throw ($reply | ConvertTo-Json -Compress) }
    return $reply.result
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}
try {
    # The installer normally places the CLI next to the app; a solution build keeps separate outputs.
    if (-not $Portable) {
        Get-ChildItem -LiteralPath (Split-Path $ctl) -Filter 'agwintermctl.*' | Copy-Item -Destination (Split-Path $exe)
    }
    if ($NativeHooks -and -not $Portable) {
        Copy-Item -LiteralPath (Join-Path $repo 'native/target/release/agwinterm-hook.exe') -Destination (Split-Path $exe) -Force
        foreach ($arch in @('x64', 'arm64')) {
            Copy-Item -LiteralPath (Join-Path $repo "artifacts/wsl-hook/agwinterm-hook-linux-$arch") -Destination (Split-Path $exe) -Force
        }
    }
    New-Item -ItemType Directory -Path $state | Out-Null
    & wsl @selectors --exec mkdir -p $linuxHome
    if ($LASTEXITCODE -ne 0) { throw 'Could not create disposable Linux HOME' }
    $profileEnv = @{ HOME = $linuxHome; ZDOTDIR = $linuxHome; CLAUDE_CONFIG_DIR = "$linuxHome/.claude"; CODEX_HOME = "$linuxHome/.codex"; XDG_CONFIG_HOME = "$linuxHome/.config" }
    if ($NoPython) {
        & wsl @selectors --exec sh -c 'mkdir -p "$1/tools"; for tool in wslpath uname mktemp cp chmod rm rmdir mkdir sh bash sleep env; do ln -s "$(command -v "$tool")" "$1/tools/$tool"; done' setup $linuxHome
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create Python-free PATH' }
        $profileEnv.PATH = "$linuxHome/tools"
    }
    @{ default = 'WSL test'; profiles = @(@{ name = 'WSL test'; command = 'wsl.exe'; args = $selectors + @('--exec', 'bash', '--noprofile', '-i'); cwd = $linuxHome; env = $profileEnv }, @{name='C0 Windows';command='cmd.exe';args=@('/d');cwd=$state}) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $state 'profiles.json') -Encoding utf8
    'new-session-dir = current' | Set-Content -LiteralPath (Join-Path $state 'agwinterm.conf')
    $process = Start-Process -FilePath $exe -ArgumentList @('--app-id', $id, '--pipe', $id, '--no-restore', '--default-session-host', 'inprocess') -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 300
        try { $null = Ctl @('ping'); break } catch { if ($i -eq 29) { throw } }
    }
    $tree = Ctl @('tree')
    $session = $tree.workspaces[0].sessions[0].id
    Check ($tree.workspaces[0].sessions[0].name -eq $id) 'Default session label uses launch directory basename'
    Check ($null -eq $tree.workspaces[0].sessions[0].customName) 'Automatic directory label is not a custom name'
    Check ($tree.workspaces[0].sessions[0].paneCwds.$session -eq $linuxHome) 'Tree exposes launch cwd by pane ID'
    try { $null = Ctl @('session', 'type', ('printf "TEST_HOME=%s\n" "$HOME"' + [char]13), '--target', $session) }
    catch { throw ("Initial shell failed: " + (Ctl @('session', 'text', '--target', $session))) }
    Start-Sleep -Seconds 1
    $initial = Ctl @('session', 'text', '--target', $session)
    Check (($initial -replace '\s', '').Contains("TEST_HOME=$linuxHome")) 'Linux HOME is isolated before any installer runs'
    if ($NoPython) {
        $null = Ctl @('session', 'type', ('if command -v python3; then printf "PYTHON_%s\n" PRESENT; else printf "PYTHON_%s\n" ABSENT; fi' + [char]13), '--target', $session)
        Start-Sleep -Seconds 1
        Check ((Ctl @('session', 'text', '--target', $session)).Contains('PYTHON_ABSENT')) 'Python is absent from the integration PATH'
    }
    foreach ($kind in @('cli', 'shell', 'hooks', 'skill')) {
        $result = Ctl @('install', $kind, '--target', $session)
        Check ($result -match 'WSL' -and $result -notmatch 'refused') "WSL $kind installer uses Linux HOME"
        if ($kind -eq 'hooks') { Check ($result.Contains("$linuxHome/.claude/settings.json")) 'Hook destinations remain inside test HOME' }
        if ($kind -eq 'hooks' -and $NativeHooks) { Check ($result.Contains('Hook runtime: native Rust')) 'Installer selects the native Linux runtime' }
    }
    $null = Ctl @('session', 'type', ('source "$HOME/.bashrc"; printf "ENV=%s/%s\n" "$AGWINTERM_ENABLED" "$AGWINTERM_PIPE"; agwintermctl ping' + [char]13), '--target', $session)
    Start-Sleep -Seconds 2
    $screen = Ctl @('session', 'text', '--target', $session)
    Check (($screen -replace '\s', '') -match "ENV=1/$id") 'Identity crosses WSLENV'
    Check ($screen -match 'agwinterm \d') 'Linux CLI reaches the private Windows control pipe'
    $null = Ctl @('session', 'type', ('mkdir -p "$HOME/path with spaces"; cd "$HOME/path with spaces"' + [char]13), '--target', $session)
    Start-Sleep -Seconds 1
    $row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session
    Check ($row.name -eq 'path with spaces') 'Automatic label follows live OSC cwd'
    Check ($row.paneCwds.$session -eq "$linuxHome/path with spaces") 'Tree exposes live Linux cwd'
    $null = Ctl @('session', 'split', 'on', '--target', $session)
    Start-Sleep -Seconds 1
    $row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session
    $split = $row.paneIds | Where-Object { $_ -ne $session }
    Check ($row.paneCwds.$split -eq "$linuxHome/path with spaces") 'Split inherits live cwd (#354)'
    $null = Ctl @('session', 'type', ('source "$HOME/.bashrc"; cd /tmp' + [char]13), '--target', $split)
    Start-Sleep -Seconds 1
    $row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session
    Check ($row.name -eq 'tmp') 'Label follows focused split pane'
    Check ($row.paneCwds.$split -eq '/tmp' -and $row.paneCwds.$session -eq "$linuxHome/path with spaces") 'Tree distinguishes split pane directories'
    $null = Ctl @('session', 'split', 'off', '--target', $session)
    $copy = Ctl @('session', 'duplicate', '--target', $session)
    Start-Sleep -Seconds 1
    $null = Ctl @('session', 'type', ('printf "PWD=%s\n" "$PWD"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $screen = Ctl @('session', 'text', '--target', $copy)
    Check (($screen -replace '\s', '').Contains("PWD=$linuxHome/pathwithspaces")) "Duplicate opens the Linux cwd ($screen)"
    $null = Ctl @('session', 'rename', 'WSL explicit task', '--target', $copy)
    $null = Ctl @('session', 'type', ('source "$HOME/.bashrc"; cd /tmp' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy
    Check ($row.name -eq 'WSL explicit task' -and $row.customName -eq 'WSL explicit task') 'Explicit name is reported separately from directory'
    Check ($row.paneCwds.$copy -eq '/tmp') 'Explicit name stays pinned after cd'
    $null = Ctl @('session', 'type', ('printf "\033]2;Claude task one\007"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy
    Check ($row.name -eq 'WSL explicit task') 'Pinned name overrides program OSC title'
    $reset = Ctl @('session', 'rename', '--clear', '--target', $copy)
    Check ($reset.name -eq 'Claude task one') 'Clear returns current automatic program title'
    $null = Ctl @('session', 'type', ('printf "\033]2;Claude task two\007"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy
    Check ($row.name -eq 'Claude task two' -and $null -eq $row.customName) 'Unpinned name follows subsequent OSC title changes'
    $null = Ctl @('session', 'type', ('printf "\033]2;\007"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy
    Check ($row.name -eq 'tmp') 'Empty program title falls back to directory'
    $null = Ctl @('session', 'rename', 'WSL explicit task', '--target', $copy)
    $null = Ctl @('session', 'type', ('cd "$HOME/path with spaces"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $saved = Get-ChildItem -LiteralPath (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    Check (($saved -join '').Contains("$linuxHome/path with spaces")) 'Persistence retains Linux cwd'
    # C0: Windows remains selected while a command addresses the Linux source. Each
    # mode writes an observed process context, not just an echoed control request.
    $windowsSession = Ctl @('session','new','--name','C0 Windows visible','--profile','C0 Windows')
    $expectedUser = ((& wsl @selectors --exec id -un) -join '').Trim()
    foreach ($mode in 'send','detached','overlay','new') {
        $null = Ctl @('session','select',$windowsSession)
        $contextFile = "$linuxHome/c0-$mode.txt"
        $identity = if ($mode -eq 'send') { "'{AGW_PANE_ID}' '{AGW_CWD}'" } else { '"$AGW_PANE_ID" "$AGW_CWD"' }
        $body = 'printf ''%s\n'' "$PWD" "$(id -un)" "$WSL_DISTRO_NAME" ' + $identity + " > '$contextFile'"
        if ($mode -eq 'overlay') { $body += '; sleep 3' }
        $null = Ctl @('command','run',$body,'--mode',$mode,'--target',$copy)
        $lines = @()
        for ($attempt=0; $attempt -lt 30; $attempt++) {
            $lines = @(& wsl @selectors --exec sh -c 'test -f "$1" && cat "$1"' c0 $contextFile)
            if ($lines.Count -ge 5) { break }
            Start-Sleep -Milliseconds 100
        }
        Check ($lines.Count -eq 5 -and $lines[0] -eq "$linuxHome/path with spaces" -and $lines[1] -eq $expectedUser -and $lines[2] -eq $Distro -and $lines[3] -eq $copy -and $lines[4] -eq "$linuxHome/path with spaces") "C0 $mode preserves target distro/user/Linux cwd/AGW identity"
        if ($mode -ne 'new') {
            Check ([bool]((Ctl @('tree')).workspaces.sessions | Where-Object { $_.id -eq $windowsSession -and $_.active })) "C0 $mode never selects source"
        }
        if ($mode -eq 'overlay') { $null = Ctl @('session','overlay','close','--target',$copy) }
    }
    $null = Ctl @('session','close',$windowsSession)
    $null = Ctl @('session','select',$copy)
    $fixture = (& wsl -d $Distro --exec wslpath -u (Join-Path $PSScriptRoot 'wsl-agent.py')).Trim()
    # Python is only the test's fake agent, explicitly outside the integration PATH.
    # Observe native Linux hook transitions through the installed CLI/Windows interop path.
    $observe = '(agwintermctl session wait completed --scope pane --wait-timeout 15 --json < /dev/null > "$HOME/c1-match.json"; printf "%s" "$?" > "$HOME/c1-match.code") & (agwintermctl events --watch --kind status --timeout 1 --json < /dev/null > "$HOME/c1-watch.jsonl" 2> "$HOME/c1-watch.err"; printf "%s" "$?" > "$HOME/c1-watch.code") &'
    $null = Ctl @('session','type',($observe + [char]13),'--target',$copy)
    Start-Sleep -Milliseconds 300
    $agentCommand = "bash -c 'exec -a codex /usr/bin/python3 `"$fixture`" --dangerously-bypass-approvals-and-sandbox'"
    $null = Ctl @('session', 'type', ($agentCommand + [char]13), '--target', $copy)
    Start-Sleep -Seconds 3
    $screen = Ctl @('session', 'text', '--target', $copy)
    Check ($screen.Contains('WSL_HOOKS_FINISHED')) 'Linux agent hooks finish successfully'
    $saved = Get-ChildItem -LiteralPath (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    Check (($saved -join '').Contains('codex resume wsl-test-conversation --dangerously-bypass-approvals-and-sandbox')) 'Linux ancestry binds the session ID and permission flags'
    $tree = Ctl @('tree')
    $copiedSession = $tree.workspaces.sessions | Where-Object id -eq $copy
    Check ($copiedSession.status -eq 'completed') 'Linux Stop hook updates pane status'
    $matchRaw = (& wsl @selectors --exec cat "$linuxHome/c1-match.json") -join "`n"
    $matchCode = (& wsl @selectors --exec cat "$linuxHome/c1-match.code") -join ''
    $matchReply = $matchRaw | ConvertFrom-Json
    if ($matchCode.Trim() -ne '0') { Write-Host (Ctl @('session','text','--target',$copy)); Write-Host $matchRaw }
    Check ($matchCode.Trim() -eq '0' -and $matchReply.result.outcome -eq 'matched' -and $matchReply.result.source -eq 'transition') 'C1 WSL wait latches native hook completion'
    $watchRaw = @(& wsl @selectors --exec cat "$linuxHome/c1-watch.jsonl")
    $watchCode = (& wsl @selectors --exec cat "$linuxHome/c1-watch.code") -join ''
    Check ($watchCode.Trim() -eq '1' -and $watchRaw.Count -ge 1) 'C1 WSL watch flushes pages and terminates on transport deadline'
    $timeoutCommand = 'agwintermctl session wait blocked --scope pane --wait-timeout 1 --json > "$HOME/c1-timeout.json"; printf "%s" "$?" > "$HOME/c1-timeout.code"'
    $null = Ctl @('session','type',($timeoutCommand + [char]13),'--target',$copy)
    Start-Sleep -Seconds 2
    $timeoutCode = (& wsl @selectors --exec cat "$linuxHome/c1-timeout.code") -join ''
    $timeoutReply = ((& wsl @selectors --exec cat "$linuxHome/c1-timeout.json") -join "`n") | ConvertFrom-Json
    Check ($timeoutCode.Trim() -eq '3' -and $timeoutReply.result.outcome -eq 'timeout') 'C1 WSL preserves confirmed wait timeout exit 3'
    $cancelCommand = 'agwintermctl session wait blocked --scope pane --wait-timeout 0 --json > "$HOME/c1-cancel.json" 2> "$HOME/c1-cancel.err"; printf "%s" "$?" > "$HOME/c1-cancel.code"'
    $null = Ctl @('session','type',($cancelCommand + [char]13),'--target',$copy)
    Start-Sleep -Milliseconds 700
    $null = Ctl @('session','type',([string][char]3),'--allow-control','--target',$copy)
    Start-Sleep -Seconds 2
    $cancelRaw = ((& wsl @selectors --exec cat "$linuxHome/c1-cancel.json") -join "`n")
    $cancelCode = ((& wsl @selectors --exec cat "$linuxHome/c1-cancel.code") -join '').Trim()
    if (-not $cancelRaw) { Write-Host (Ctl @('session','text','--target',$copy)); Write-Host "C1 cancellation wrapper exit=$cancelCode" }
    $cancelReply = $cancelRaw | ConvertFrom-Json
    Check ($cancelCode -eq '130' -and $cancelReply.code -eq 'cancelled' -and $cancelReply.source -eq 'client') 'C1 WSL terminal Ctrl+C cancels the pending Windows CLI wait with exit 130'
    $cancelWatch = 'agwintermctl events --watch --json > "$HOME/c1-watch-cancel.jsonl" 2> "$HOME/c1-watch-cancel.err"; printf "%s" "$?" > "$HOME/c1-watch-cancel.code"'
    $null = Ctl @('session','type',($cancelWatch + [char]13),'--target',$copy)
    Start-Sleep -Milliseconds 700
    $null = Ctl @('session','type',([string][char]3),'--allow-control','--target',$copy)
    Start-Sleep -Seconds 1
    $watchCancelCode = ((& wsl @selectors --exec cat "$linuxHome/c1-watch-cancel.code") -join '').Trim()
    Check ($watchCancelCode -eq '130') 'C1 WSL terminal Ctrl+C terminates watch with exit 130'
    $askNoBlock = 'printf ''%s'' ''{"title":"WSL question"}'' | agwintermctl ask --no-block > "$HOME/c3-ask-id.json"; printf "%s" "$?" > "$HOME/c3-ask-open.code"'
    $null = Ctl @('session','type',($askNoBlock + [char]13),'--target',$copy)
    Start-Sleep -Seconds 1
    $askId = (((& wsl @selectors --exec cat "$linuxHome/c3-ask-id.json") -join "`n") | ConvertFrom-Json).id
    $askPending = (& $ctl ask result $askId --pipe $id) | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 1 -and $askPending.result -eq 'pending') 'C3 WSL no-block form returns a usable exact ID'
    $null = & $ctl ask cancel $askId --pipe $id
    $askBlocking = 'printf ''%s'' ''{"title":"WSL cancellable question"}'' | agwintermctl ask > "$HOME/c3-ask-answer.json" 2> "$HOME/c3-ask.err"; printf "%s" "$?" > "$HOME/c3-ask.code"'
    $null = Ctl @('session','type',($askBlocking + [char]13),'--target',$copy)
    Start-Sleep -Seconds 1
    $null = Ctl @('session','type',([string][char]3),'--allow-control','--target',$copy)
    Start-Sleep -Seconds 2
    $askCode = ((& wsl @selectors --exec cat "$linuxHome/c3-ask.code") -join '').Trim()
    Check ($askCode -eq '130') 'C3 WSL real terminal Ctrl+C exits ask with 130'
    $probe = '{}' | & $ctl ask --no-block --pipe $id | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $probe.id) 'C3 WSL cancellation releases the exact owned form slot'
    $null = & $ctl ask cancel $probe.id --pipe $id
    $overlay = Ctl @('session', 'overlay', 'open', '--command', 'printf "OVERLAY_%s\n" "LINUX"; sleep 2', '--wait', '--target', $copy)
    Start-Sleep -Seconds 1
    $overlayText = Ctl @('session', 'overlay', 'text', '--target', $copy)
    Check ($overlayText.text -match 'OVERLAY_LINUX') 'Overlay commands run in Linux'
    $null = Ctl @('session', 'overlay', 'close', '--target', $copy)
    $null = Ctl @('session', 'select', $session)
    $c2Command = 'printf "C2_CWD=%s\n" "$PWD"; sleep 30'
    $c2Overlay = Ctl @('session','overlay','open','--command',$c2Command,'--cwd',"$linuxHome/path with spaces",'--target',$copy,'--width','420','--height','240','--background-color','#123456','--wait')
    Start-Sleep -Seconds 1
    $c2Text = (Ctl @('session','overlay','text','--target',$c2Overlay)).text
    Check (($c2Text -replace '\s','').Contains("C2_CWD=$linuxHome/pathwithspaces")) 'C2 explicit overlay cwd preserves Linux paths with spaces'
    Check (((Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session).active) 'C2 background open does not select the owner'
    $badCwd = (& $ctl session overlay open --command 'echo SHOULD_NOT_RUN' --cwd "$linuxHome/missing ' directory" --target $copy --pipe $id --json) | ConvertFrom-Json
    Check (-not $badCwd.ok -and $LASTEXITCODE -eq 1) 'C2 nonexistent WSL cwd refuses replacement'
    $preserved = ((Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy).overlayDetails[0]
    Check ($preserved.id -eq $c2Overlay -and -not $preserved.exited) 'C2 invalid WSL cwd preserves the running overlay identity and process'
    $null = Ctl @('session','overlay','hide','--target',$c2Overlay)
    Check (((Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy).overlayDetails[0].hidden) 'C2 WSL overlay hides without losing its identity'
    $null = Ctl @('session','overlay','open','--command','sleep 2','--target',$copy,'--follow','--wait')
    Check (((Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy).active) 'C2 follow selects the WSL owner explicitly'
    $null = Ctl @('session','overlay','close','--target',$copy)
    $null = Ctl @('session', 'bind', 'none', '--target', $copy)
    $c4File = 'printf ''# Linux HUD\n\n**Ready** from a file with spaces\n'' > "$HOME/hud message.md"; agwintermctl session hud --file "$(wslpath -w "$HOME/hud message.md")" --markdown --sticky --no-frame --position top --size-percent 100 --font-size 18 --json > "$HOME/c4-hud.json"; printf "%s" "$?" > "$HOME/c4-hud.code"'
    $null = Ctl @('session','type',($c4File + [char]13),'--target',$copy)
    Start-Sleep -Seconds 1
    $c4Code = ((& wsl @selectors --exec cat "$linuxHome/c4-hud.code") -join '').Trim()
    $c4Hud = ((Ctl @('tree')).workspaces.sessions | Where-Object id -eq $copy).hud
    Check ($c4Code -eq '0' -and $c4Hud.markdown -and $c4Hud.message.Contains('Linux HUD') -and $c4Hud.fontSize -eq 18 -and $c4Hud.sticky -and -not $c4Hud.frame) 'C4 WSL explicit file conversion preserves UTF-8 Markdown and paths with spaces'
    $null = Ctl @('session','hud','close','--target',$copy)
    $c6 = Ctl @('session','new','--after',$copy,'--profile','WSL test','--cwd',"$linuxHome/path with spaces",'--name','C6 anchored','--no-select')
    $c6Order = @((Ctl @('tree')).workspaces.sessions.id)
    Check ([array]::IndexOf($c6Order,$c6) -eq ([array]::IndexOf($c6Order,$copy)+1)) 'C6 anchored WSL creation preserves requested order'
    Start-Sleep -Seconds 1
    $null = Ctl @('session','type',('printf "C6_%s=%s\n" "CWD" "$PWD"' + [char]13),'--target',$c6)
    Start-Sleep -Seconds 1
    Check (((Ctl @('session','text','--target',$c6)) -replace '\s','').Contains("C6_CWD=$linuxHome/pathwithspaces")) 'C6 anchored WSL creation preserves Linux cwd with spaces'
    $null = Ctl @('session','move','--target',$c6,'--before',$copy)
    $c6Order = @((Ctl @('tree')).workspaces.sessions.id)
    Check ([array]::IndexOf($c6Order,$c6) -eq ([array]::IndexOf($c6Order,$copy)-1)) 'C6 anchored WSL move preserves identity and order'
    $null = Ctl @('session','close','--target',$c6)
    $c5 = Ctl @('session','duplicate','--target',$copy)
    $c5Right = Ctl @('session','split','on','--axis','horizontal','--target',$c5)
    Start-Sleep -Seconds 1
    $null = Ctl @('session','type',('cd /tmp' + [char]13),'--target',$c5Right)
    Start-Sleep -Seconds 1
    $null = Ctl @('session','restore','echo C5_PIN_ONLY','--target',$c5Right)
    $null = Ctl @('session','swap','--target',$c5)
    $c5Before = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $c5
    $null = Ctl @('session','close','--target',$c5)
    $c5Entry = @((Ctl @('recent','list')).entries)[0]
    $null = Ctl @('config', 'set', 'restore-commands', 'true')
    $null = Ctl @('session', 'type', ('sleep 15' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $capture = Ctl @('restore', 'capture', '--target', $copy)
    Check (($capture | ConvertTo-Json -Depth 10).Contains('sleep 15')) 'Foreground capture reads Linux processes'
    Check $capture.replayOnRestore 'Captured commands are enabled for restart'
    Start-Sleep -Seconds 1
    $saved = Get-ChildItem -LiteralPath (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    Check (-not ($saved -join '').Contains('wsl-test-conversation')) 'Test agent binding is cleared before restart'
    $observationBeforeRestart = Ctl @('events')
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()
    $process = Start-Process -FilePath $exe -ArgumentList @('--app-id', $id, '--pipe', $id, '--default-session-host', 'inprocess') -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 300
        try { $null = Ctl @('ping'); break } catch { if ($i -eq 29) { throw } }
    }
    # Ping can succeed while restore is still creating the first window.
    for ($i = 0; $i -lt 30; $i++) {
        try { $observationAfterRestart = Ctl @('events'); break }
        catch { if ($i -eq 29) { throw }; Start-Sleep -Milliseconds 300 }
    }
    Check ($observationBeforeRestart.run -ne $observationAfterRestart.run) 'C1 app restart changes observation run identity'
    $oldRunReply = ((& $ctl events --run $observationBeforeRestart.run --since $observationBeforeRestart.cursor --pipe $id --json) -join "`n") | ConvertFrom-Json
    Check (-not $oldRunReply.ok -and $oldRunReply.code -eq 'run-mismatch') 'C1 refuses an old cursor/run after real restart'
    # A fresh capture proves a Linux child is running in the restored pane, rather than
    # merely finding the old command in persisted scrollback. In-process shells cannot be adopted.
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        $capture = Ctl @('restore', 'capture', '--target', $copy)
        if (($capture | ConvertTo-Json -Depth 10).Contains('sleep 15')) { break }
    }
    Check (($capture | ConvertTo-Json -Depth 10).Contains('sleep 15')) 'App restart replays the captured Linux command'
    # Let the restored child finish; a raw Ctrl+C through ConPTY is not reliable.
    Start-Sleep -Seconds 16
    $null = Ctl @('session', 'type', ('printf "RESTORED_%s=%s\n" "CWD" "$PWD"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $screen = Ctl @('session', 'text', '--target', $copy)
    Check (($screen -replace '\s', '').Contains("RESTORED_CWD=$linuxHome/pathwithspaces")) "App restart restores the distribution and Linux directory ($screen)"
    $rows = (Ctl @('tree')).workspaces.sessions
    $row = $rows | Where-Object id -eq $copy
    Check ($row.name -eq 'WSL explicit task' -and $row.customName -eq 'WSL explicit task') 'Explicit name survives restart'
    $row = $rows | Where-Object id -eq $session
    Check ($row.name -eq 'path with spaces' -and $null -eq $row.customName) 'Automatic name stays automatic after restart'
    Write-Host 'WSL control integration passed.'
    $c5AfterRestart = @((Ctl @('recent','list')).entries | Where-Object id -eq $c5Entry.id)
    Check ($c5AfterRestart.Count -eq 1) 'C5 recent entry survives actual process restart'
    $c5Opened = Ctl @('recent','open',$c5Entry.id)
    $c5Row = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $c5
    Check ($c5Opened.outcome -eq 'completed' -and $c5Row.axis -eq 'horizontal' -and ($c5Row.paneIds -join ',') -eq ($c5Before.paneIds -join ',')) 'C5 WSL swapped split identities and axis restore'
    Start-Sleep -Seconds 1
    $null = Ctl @('session','type',('printf "C5_%s=%s\n" "CWD" "$PWD"' + [char]13),'--target',$c5)
    Start-Sleep -Seconds 1
    $c5Text = Ctl @('session','text','--target',$c5)
    Check (($c5Text -replace '\s','').Contains("C5_CWD=$linuxHome/pathwithspaces")) 'C5 restores the WSL distribution user and directory'
    Check (($c5Row.restoreCommands|ConvertTo-Json -Compress) -eq ($c5Before.restoreCommands|ConvertTo-Json -Compress)) 'C5 preserves WSL restore pins'
    Check ($c5Row.paneCwds.$c5Right -eq '/tmp' -and $c5Row.paneCwds.$c5 -eq "$linuxHome/path with spaces") 'C5 restores distinct WSL pane directories'
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    # Generated paths are confined to test namespaces; never accept a user-supplied deletion path.
    $expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA $id))
    if ($state -eq $expected -and $id -match '^agwinterm-wsl-test-[a-f0-9]{32}$') {
        if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Recurse -Force }
        & wsl @selectors --exec python3 -c 'import shutil,sys; p=sys.argv[1]; assert p.startswith("/tmp/agwinterm-wsl-test-") and "/" not in p[5:]; shutil.rmtree(p)' $linuxHome
    }
}

