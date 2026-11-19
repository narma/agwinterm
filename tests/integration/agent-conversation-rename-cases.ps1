param(
    [string]$Distro,
    [string]$Codex,
    [string]$Claude,
    [string]$Exe,
    [string]$Ctl
)
$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AgentRenameWindow {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string name);
    public static IntPtr Find(IntPtr parent, string cls) => FindWindowExW(parent, IntPtr.Zero, cls, null);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, string text);
    delegate bool Visit(IntPtr h, IntPtr arg);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, Visit visit, IntPtr arg);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desktop, Visit visit, IntPtr arg);
    public static IntPtr MainWindow(uint pid) {
        IntPtr result = IntPtr.Zero;
        EnumDesktopWindows(GetThreadDesktop(GetCurrentThreadId()), (h, unused) => {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid) return true;
            var name = new System.Text.StringBuilder(128);
            GetClassNameW(h, name, name.Capacity);
            if (name.ToString() != "AgwintermWin32") return true;
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }
    public static string[] TopClasses(IntPtr main) {
        var classes = new System.Collections.Generic.List<string>();
        EnumThreadWindows(GetWindowThreadProcessId(main, out _), (h, unused) => {
            var name = new System.Text.StringBuilder(128);
            GetClassNameW(h, name, name.Capacity);
            classes.Add(name.ToString());
            return true;
        }, IntPtr.Zero);
        return classes.ToArray();
    }
}
'@

$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not $Exe) { $Exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net11.0-windows/win-x64/Agwinterm.Win32.exe' }
if (-not $Ctl) { $Ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net11.0-windows/agwintermctl.exe' }
$run = 'agwinterm-ar-' + [guid]::NewGuid().ToString('N').Substring(0,12)
$state = Join-Path $env:LOCALAPPDATA $run
$store = if ($Distro) { "/tmp/$run" } else { $state }
$project = "$store/project"
$settings = @{
    CODEX_HOME = "$store/codex"
    CLAUDE_CONFIG_DIR = "$store/claude"
    OPENAI_API_KEY = 'fixture-key'
    ANTHROPIC_API_KEY = 'fixture-key'
    OPENAI_BASE_URL = 'http://127.0.0.1:9/v1'
    ANTHROPIC_BASE_URL = 'http://127.0.0.1:9'
    DISABLE_TELEMETRY = '1'
    DISABLE_AUTOUPDATER = '1'
}
$process = $null

function Wait-For([string]$label, [scriptblock]$condition) {
    for ($i = 0; $i -lt 100; $i++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $label"
}

function Control([string[]]$arguments) {
    $raw = (& $ctl @arguments --pipe $run --json) -join [char]10
    $reply = $raw | ConvertFrom-Json
    if (-not $reply.ok) { throw "Control failed: $raw" }
    $reply.result
}

function Fixture([string]$path, [string[]]$lines) {
    if ($Distro) {
        ($lines -join [char]10) + [char]10 | & wsl -d $Distro --exec /bin/sh -c 'mkdir -p "$(dirname "$1")" && tr -d "\r" > "$1"' fixture $path
        if ($LASTEXITCODE -ne 0) { throw "Could not write Linux fixture $path" }
    } else {
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        [IO.File]::WriteAllLines($path, $lines, [Text.UTF8Encoding]::new($false))
    }
}

function Read-Session([string]$pane) {
    $snapshot = Get-ChildItem -LiteralPath "$state/windows" -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $snapshot) { return }
    try {
        $file = [IO.File]::Open($snapshot.FullName, 'Open', 'Read', [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $reader = [IO.StreamReader]::new($file)
        try { ($reader.ReadToEnd() | ConvertFrom-Json).Workspaces.Sessions | Where-Object { $_.Panes.Id -contains $pane } }
        finally { $reader.Dispose() }
    } catch [IO.IOException] { }
}

function Rename-InSidebar([string]$pane, [string]$id, [string]$newName, [bool]$fromAgents) {
    $null = Wait-For 'bound agent conversation' { (Read-Session $pane).Panes.Conversation.Id -contains $id }
    [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]0x75, [IntPtr]1) # F6
    if ($fromAgents) {
        # Select the only agent via Tab and Home, then open its context menu.
        foreach ($key in @(9, 0x24)) {
            [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]$key, [IntPtr]1)
        }
        [void][AgentRenameWindow]::UpdateWindow($hwnd)
        [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]0x5d, [IntPtr]1)
        $null = Wait-For 'Agents context menu' {
            [AgentRenameWindow]::TopClasses($hwnd) -contains 'agwinterm-menu'
        }
        # Open, Pin, Copy ID, Rename agent conversation.
        [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]0x24, [IntPtr]1)
        for ($i = 0; $i -lt 3; $i++) {
            [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]0x28, [IntPtr]1)
        }
        [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]13, [IntPtr]1)
    } else {
        [void][AgentRenameWindow]::UpdateWindow($hwnd)
        [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]0x71, [IntPtr]1) # F2
    }
    $edit = Wait-For 'agent rename editor' {
        $handle = [AgentRenameWindow]::Find($hwnd, 'Edit')
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    [void][AgentRenameWindow]::SendMessageW($edit, 0x0c, [IntPtr]::Zero, $newName)
    [void][AgentRenameWindow]::SendMessageW($edit, 0x100, [IntPtr]13, [IntPtr]1)
    $null = Wait-For "confirmed sidebar and agent title $newName" {
        $renamed = Read-Session $pane
        $saved = $renamed.Panes.Conversation | Where-Object Id -eq $id
        $renamed.CustomName -ceq $newName -and $saved.Title -ceq $newName
    }
    [void][AgentRenameWindow]::SendMessageW($hwnd, 0x100, [IntPtr]27, [IntPtr]1)
}

function Start-InPane([string]$pane, [string]$command) {
    $null = Control @('session','type',($command + [char]13),'--target',$pane)
}

function Wait-PaneShell([string]$pane) {
    Start-InPane $pane 'echo ARREADY'
    try {
        $null = Wait-For 'fixture shell' {
            [string](Control @('session','text','--target',$pane)) -match '(?m)^ARREADY\s*$'
        }
    } catch {
        Write-Host ([string](Control @('session','text','--target',$pane)))
        throw
    }
}

try {
    New-Item -ItemType Directory -Path $state | Out-Null
    if ($Distro) {
        & wsl -d $Distro --exec mkdir -p $project
        if ($LASTEXITCODE -ne 0) { throw 'Could not create isolated WSL project' }
        $profile = @{command='wsl.exe';args=@('-d',$Distro,'--exec','bash','--noprofile','-i')}
        $agentBin = (& wsl -d $Distro --exec /bin/bash -lc 'dirname "$(command -v codex)"').Trim()
        $claudeBin = (& wsl -d $Distro --exec /bin/bash -lc 'dirname "$(command -v claude)"').Trim()
        $settings.PATH = "$($agentBin):$($claudeBin):/usr/local/bin:/usr/bin:/bin"
    } else {
        New-Item -ItemType Directory -Path $project | Out-Null
        $project = [IO.Path]::GetFullPath($project)
        $bin = (New-Item -ItemType Directory -Path (Join-Path $env:TEMP $run)).FullName
        Copy-Item -LiteralPath $Codex -Destination "$bin/codex.exe"
        Copy-Item -LiteralPath $Claude -Destination "$bin/claude.exe"
        $profile = @{command='cmd.exe';args=@('/d')}
        $settings.PATH = "$bin;$env:PATH"
    }
    $profile.name = 'Agent rename fixture'
    $profile.cwd = $project
    $profile.env = $settings
    @{default=$profile.name;profiles=@($profile)} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath "$state/profiles.json"
    'sidebar-copy-id = conversation' | Set-Content -LiteralPath "$state/agwinterm.conf"
    $codexId = [guid]::NewGuid().ToString()
    $claudeId = [guid]::NewGuid().ToString()
    $now = [DateTime]::UtcNow
    $timestamp = $now.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $codexFile = "$($settings.CODEX_HOME)/sessions/$($now.ToString('yyyy/MM/dd'))/rollout-$($now.ToString('yyyy-MM-ddTHH-mm-ss'))-$codexId.jsonl"
    $meta = @{timestamp=$timestamp;type='session_meta';payload=@{id=$codexId;timestamp=$timestamp;cwd=$project;originator='codex_cli_rs';cli_version='0.170.0';source='cli';model_provider='openai'}}
    $turn = @{timestamp=$timestamp;type='event_msg';payload=@{type='user_message';message='Isolated Codex fixture';images=@();local_images=@();text_elements=@()}}
    $item = @{timestamp=$timestamp;type='response_item';payload=@{type='message';role='user';content=@(@{type='input_text';text='Isolated Codex fixture'})}}
    Fixture $codexFile @($meta,$item,$turn | ForEach-Object { ConvertTo-Json $_ -Depth 8 -Compress })
    $tomlProject = ConvertTo-Json $project -Compress
    Fixture "$($settings.CODEX_HOME)/config.toml" @('check_for_update_on_startup = false', 'cli_auth_credentials_store = "file"',
        ('[projects.' + $tomlProject + ']'), 'trust_level = "trusted"')
    Fixture "$($settings.CODEX_HOME)/auth.json" @('{"auth_mode":"apikey","OPENAI_API_KEY":"fixture-key"}')
    $projectKey = $project -replace '[^a-zA-Z0-9-]', '-'
    $claudeFile = "$($settings.CLAUDE_CONFIG_DIR)/projects/$projectKey/$claudeId.jsonl"
    $message = @{type='user';sessionId=$claudeId;cwd=$project;timestamp=$timestamp;uuid=[guid]::NewGuid().ToString();parentUuid=$null;isSidechain=$false;message=@{role='user';content='Isolated Claude fixture'}}
    Fixture $claudeFile @((ConvertTo-Json $message -Depth 8 -Compress))

    $app = [Diagnostics.ProcessStartInfo]::new($exe)
    $app.UseShellExecute = $false
    $app.WorkingDirectory = $repo
    $app.Environment['AGWINTERM_PERF'] = "$state/ui.log"
    foreach ($arg in @('--app-id',$run,'--pipe',$run,'--no-restore','--default-session-host','inprocess')) { $app.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($app)
    $null = Wait-For 'control pipe' { try { Control @('ping') } catch { $false } }
    $hwnd = Wait-For 'application window' {
        $handle = [AgentRenameWindow]::MainWindow($process.Id)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    foreach ($agent in @('codex','claude')) {
        $id = if ($agent -eq 'codex') { $codexId } else { $claudeId }
        $oldName = "Before-$agent"
        $newName = "Renamed $agent $($id.Substring(0,8))"
        $pane = [string](Control @('session','new','--name',$oldName))
        Wait-PaneShell $pane
        $resume = if ($agent -eq 'codex') { "codex resume $id" } else { "claude --safe-mode --resume $id" }
        Start-InPane $pane $resume
        try {
            $null = Wait-For "$agent terminal UI" {
                [string](Control @('session','text','--target',$pane)) -match 'OpenAI Codex|Claude Code|Welcome to Claude'
            }
        } catch {
            Write-Host ([string](Control @('session','text','--target',$pane)))
            throw
        }
        $resumeCommand = if ($agent -eq 'codex') { "codex resume $id" } else { "claude --resume $id" }
        $null = Control @('session','bind',$resumeCommand,'--target',$pane)
        if ($agent -eq 'codex') {
            $split = [string](Control @('session','split','on','--target',$pane))
            Wait-PaneShell $split
            $null = Control @('session','bind',"claude --resume $claudeId",'--target',$split)
            $null = Wait-For 'second split conversation' {
                (Read-Session $pane).Panes.Conversation.Id -contains $claudeId
            }
            $null = Control @('session','focus','primary','--target',$pane)
        }
        Rename-InSidebar $pane $id $newName ($agent -eq 'claude')
        if ($agent -eq 'codex') {
            $other = (Read-Session $pane).Panes.Conversation | Where-Object Id -eq $claudeId
            if (-not $other -or $other.Title) { throw 'The sidebar rename changed the other split conversation' }
            Write-Host 'PASS Sidebar rename targeted only the focused split conversation'
        }
        $null = Control @('session','close','--target',$pane)
        Write-Host "PASS Renamed and immediately closed $agent pane"

        $probe = [string](Control @('session','new','--name',"Resume-$agent"))
        Wait-PaneShell $probe
        if ($agent -eq 'codex') {
            Start-InPane $probe 'codex resume --all'
            try {
                $null = Wait-For 'new name in Codex resume picker' {
                    ([string](Control @('session','text','--target',$probe))).Contains($newName)
                }
            } catch {
                Write-Host ([string](Control @('session','text','--target',$probe)))
                throw
            }
        } else {
            $empty = if ($Distro) { '/dev/null' } else { 'NUL' }
            Start-InPane $probe "claude --resume ""$newName"" --safe-mode --print --input-format stream-json --output-format stream-json --verbose < $empty"
            $null = Wait-For 'Claude resumes by the new name and returns the same ID' {
                $output = [string](Control @('session','text','--target',$probe))
                $compact = $output -replace '\s+', ''
                $compact.Contains('session_title_changed') -and $compact.Contains($id)
            }
        }
        Write-Host "PASS $agent resume found the new title after the original pane was closed"
        $null = Control @('session','close','--target',$probe)
    }
} catch {
    if ($hwnd) { Write-Host ('Application windows: ' + ([AgentRenameWindow]::TopClasses($hwnd) -join ', ')) }
    if (Test-Path -LiteralPath "$state/ui.log") {
        Select-String -LiteralPath "$state/ui.log" -Pattern 'wndproc ex|failed' | Select-Object -Last 12 | ForEach-Object { Write-Host $_.Line }
    }
    throw
} finally {
    if ($process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
}
