# Windows PowerShell 5.1, already supplied by Windows. Never run this against the
# developer's live installation from a test: fixtures use an isolated InstallDir.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$ReportPath,
    [switch]$GuiOnly,
    [ValidateRange(5,300)][int]$WaitSeconds=60
)
$ErrorActionPreference='Stop'
try {
    $InstallDir=[IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
    $gui=Join-Path $InstallDir 'Agwinterm.Win32.exe'
    $rust=Join-Path $InstallDir 'agwinterm-ptyhost.exe'
    $sessionHost=Join-Path $InstallDir 'host\Agwinterm.Host.exe'
    function InstalledProcesses {
        @(Get-CimInstance Win32_Process -Filter "Name='Agwinterm.Win32.exe' OR Name='agwinterm-ptyhost.exe' OR Name='Agwinterm.Host.exe'" |
            Where-Object { $_.ExecutablePath -and ($_.ExecutablePath -ieq $gui -or $_.ExecutablePath -ieq $rust -or
                (-not $GuiOnly -and $_.ExecutablePath -ieq $sessionHost)) })
    }
    $initial=@(InstalledProcesses)
    $uis=@($initial | Where-Object { $_.ExecutablePath -ieq $gui -and $_.CommandLine -notmatch '(?:^|\s)--pty-host(?:\s|$)' })
    $hosts=@($initial | Where-Object { $_ -notin $uis })
    if($GuiOnly -and $hosts.Count){throw 'Legacy host detected. Finish its workloads and run the full installer once.'}
    if($GuiOnly -and -not $initial.Count){exit 0}
    if($hosts.Count -gt 0 -and $uis.Count -eq 0){throw 'An old session host is running without its UI. Open the installed version once, then retry the upgrade so it can save current state.'}
    Add-Type @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class OwnerUpgradePipe {
    [DllImport("kernel32", SetLastError=true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    public static string Send(string name,string json,uint[] allowed) {
        using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous)) {
            pipe.Connect(3000);
            uint pid;
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid) || Array.IndexOf(allowed,pid)<0)
                throw new IOException("Control pipe belongs to another installation; upgrade refused.");
            using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true))
            using(var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true)) {
                writer.WriteLine(json); writer.Flush();
                var read=reader.ReadLineAsync();
                if(!read.Wait(30000)) throw new TimeoutException("Checkpoint reply timed out; upgrade refused.");
                string result=read.GetAwaiter().GetResult();
                if(result==null || result.Length>16*1024*1024) throw new IOException("Invalid checkpoint reply.");
                return result;
            }
        }
    }
}
'@
    $groups=@{}
    foreach($process in $uis) {
        $appId='agwinterm'; $pipe=$null
        if($process.CommandLine -match '(?:^|\s)--app-id\s+(?:"([^"]+)"|(\S+))') {
            $appId=if($Matches[1]){$Matches[1]}else{$Matches[2]}
        }
        if($appId -notmatch '^[A-Za-z0-9_.-]+$' -or $appId -in '.','..') { throw 'Invalid application identity; upgrade refused.' }
        if($process.CommandLine -match '(?:^|\s)--pipe\s+(?:"([^"]+)"|(\S+))') {
            $pipe=if($Matches[1]){$Matches[1]}else{$Matches[2]}
        }
        if(-not $pipe){$pipe=$appId}
        if(-not $groups.ContainsKey($pipe)) {$groups[$pipe]=@{AppId=$appId;Pids=@();Windows=@()}}
        if($groups[$pipe].AppId -ne $appId){throw 'Multiple state directories claim one UI control pipe; upgrade refused.'}
        $groups[$pipe].Pids+= [uint32]$process.ProcessId
    }
    function Request($group,[string]$pipe,[hashtable]$request) {
        $reply=[OwnerUpgradePipe]::Send($pipe,($request|ConvertTo-Json -Depth 8 -Compress),[uint32[]]$group.Pids)|ConvertFrom-Json
        if(-not $reply.ok){throw "Checkpoint/close refused: $($reply.error)"}
        return $reply.result
    }
    # A second, headless application identity must not be terminated merely
    # because it happens to use binaries from the same installation.
    $coveredHosts=@($groups.Keys | ForEach-Object {
        # Released versions derive the host namespace from --pipe when set.
        # --app-id independently names the state directory to back up.
        $_; ($_+'-rust'); ($_+'-presentation-v1'); ($_+'-owner-v1')
    })
    foreach($hostProcess in $hosts) {
        $hostIdentity=$null
        if($hostProcess.CommandLine -match '(?:^|\s)--pipe\s+(?:"([^"]+)"|(\S+))') {
            $hostIdentity=if($Matches[1]){$Matches[1]}else{$Matches[2]}
        }
        if(-not $hostIdentity -or $hostIdentity -notin $coveredHosts) {
            throw 'An old host has no matching open UI to checkpoint. Open its application identity and retry the upgrade.'
        }
    }
    # Every UI must confirm a durable checkpoint before any process is closed.
    foreach($pipe in $groups.Keys) {
        $group=$groups[$pipe]
        $group.Windows=@((Request $group $pipe @{cmd='window.list'}).windows | Where-Object open)
        foreach($window in $group.Windows) {
            $null=Request $group $pipe @{cmd='restore.capture';window=$window.id;args=@{}}
        }
    }
    function AssertNotRedirected($item) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Cannot safely back up redirected state: $($item.FullName)"}
    }
    function SavedFiles([IO.DirectoryInfo]$directory) {
        AssertNotRedirected $directory
        # Check each entry before descending; old Windows PowerShell can follow
        # junctions when recursively enumerating.
        foreach($entry in $directory.EnumerateFileSystemInfos()){
            AssertNotRedirected $entry
            if($entry -is [IO.DirectoryInfo]){SavedFiles $entry}else{$entry}
        }
    }
    $backupParent=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'agwinterm-upgrade-backups'
    if(Test-Path -LiteralPath $backupParent){AssertNotRedirected (Get-Item -LiteralPath $backupParent)}
    $backupRoot=Join-Path $backupParent ([guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $backupRoot
    $appIds=@('agwinterm')+@($groups.Values|ForEach-Object AppId) | Select-Object -Unique
    foreach($appId in $appIds) {
        $data=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $appId
        if(-not(Test-Path -LiteralPath $data)){continue}
        AssertNotRedirected (Get-Item -LiteralPath $data)
        $saved=Join-Path $backupRoot $appId; $null=New-Item -ItemType Directory -Path $saved
        $files=@(Get-ChildItem -LiteralPath $data -File | Where-Object Extension -In '.json','.conf')
        foreach($dir in 'windows','agent-completions','backgrounds') {
            $path=Join-Path $data $dir
            if(Test-Path -LiteralPath $path) {
                $files+=@(SavedFiles (Get-Item -LiteralPath $path))
            }
        }
        foreach($file in $files) {
            AssertNotRedirected $file
            $destination=Join-Path $saved $file.FullName.Substring($data.Length+1)
            $null=New-Item -ItemType Directory -Path (Split-Path $destination) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash){throw 'State backup verification failed'}
        }
    }
    @{schema=1;installation=$InstallDir;processes=@($initial|Select-Object ProcessId,ExecutablePath,CreationDate);status='checkpointed'} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backupRoot 'upgrade.json') -Encoding UTF8
    foreach($group in $groups.Values){
        if($group.Windows.Count -gt 1 -and -not(Test-Path -LiteralPath (Join-Path $backupRoot ($group.AppId+'/windows.json')))){
            throw 'Cannot preserve open windows without a backed up window index; old installation remains open.'
        }
    }
    foreach($pipe in $groups.Keys) {
        $group=$groups[$pipe]
        if($GuiOnly){$null=Request $group $pipe @{cmd='app.close-for-update'}}
        else {foreach($window in $group.Windows) { $null=Request $group $pipe @{cmd='window.close';target=$window.id} }}
    }
    $deadline=[DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        $remaining=@(InstalledProcesses | Where-Object { $_.ProcessId -in $uis.ProcessId })
        if(-not $remaining.Count){break}
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    if($remaining.Count){throw "Old UI did not close. Installation unchanged; backup: $backupRoot"}
    if($GuiOnly){
        if(@(InstalledProcesses).Count){throw 'Application restarted during GUI update; retry after closing it.'}
        Write-Output "State backup: $backupRoot"
        exit 0
    }
    # Old Windows processes are intentionally ended on this upgrade. Exact path,
    # PID and creation time must still match the checkpointed inventory.
    foreach($process in @(InstalledProcesses)) {
        $expected=@($hosts|Where-Object { $_.ProcessId -eq $process.ProcessId -and $_.CreationDate -eq $process.CreationDate })
        if($expected.Count -ne 1){throw 'Application restarted during upgrade; retry after closing it.'}
        $handle=Get-Process -Id $process.ProcessId -ErrorAction Stop
        try {
            if([Math]::Abs(($handle.StartTime-$process.CreationDate).TotalMilliseconds) -gt 1){throw 'Host identity changed during upgrade.'}
            $handle.Kill()
            if(-not $handle.WaitForExit($WaitSeconds*1000)){throw 'Old host did not terminate.'}
        } finally { $handle.Dispose() }
    }
    if(@(InstalledProcesses).Count){throw 'Old host is still running; installation refused.'}
    # Old releases only expose ordinary window.close via their control pipe.
    # Each close except the last marks that window closed in the library index.
    # Restore the checkpointed index after the old process has stopped writing.
    foreach($group in $groups.Values){
        if($group.Windows.Count -lt 2){continue}
        $index=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ($group.AppId+'/windows.json')
        $savedIndex=Join-Path $backupRoot ($group.AppId+'/windows.json')
        if(-not(Test-Path -LiteralPath $savedIndex)){throw "Missing checkpointed window index. Backup: $backupRoot"}
        $temp=$index+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
        try {
            Copy-Item -LiteralPath $savedIndex -Destination $temp
            if([IO.File]::Exists($index)){[IO.File]::Replace($temp,$index,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temp,$index)}
        } finally {if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp}}
    }
    if($ReportPath){"State saved and old sessions closed. Backup: $backupRoot"|Set-Content -LiteralPath $ReportPath -Encoding UTF8}
    Write-Output "State backup: $backupRoot"
    exit 0
} catch {
    if($ReportPath){$_.Exception.Message|Set-Content -LiteralPath $ReportPath -Encoding UTF8}
    Write-Error $_
    exit 1
}
