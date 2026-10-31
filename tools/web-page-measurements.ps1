param(
    [string]$Exe = "$PSScriptRoot/../src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
    [string]$Ctl = "$PSScriptRoot/../src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe",
    [string]$Output = "$PSScriptRoot/../artifacts/webview2-page-measurements.json"
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path; $Ctl = (Resolve-Path $Ctl).Path
$identity = 'agwinterm-web-measure-' + [guid]::NewGuid().ToString('N')
$directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $identity
New-Item -ItemType Directory $directory | Out-Null
@{ default='Measure'; profiles=@(@{name='Measure';command='cmd.exe';args=@('/d','/q');cwd=$directory}) } | ConvertTo-Json -Depth 5 | Set-Content "$directory/profiles.json"
"session-host = in-process`nemulator-core = managed" | Set-Content "$directory/agwinterm.conf"
$source = Join-Path $directory 'measure.html'
@'
<!doctype html><meta charset="utf-8"><title>Web page measurement</title>
<h1>Local page measurement</h1><input aria-label="Paint timing" readonly><textarea>Unsaved draft</textarea>
<script>new PerformanceObserver(list=>{for(const e of list.getEntries())if(e.name==='first-contentful-paint')document.querySelector('input').value=String(e.startTime)}).observe({type:'paint',buffered:true});</script>
'@ | Set-Content -Encoding utf8 $source
$saved = @{}
foreach ($key in @('AGWINTERM_APP_ID','AGWINTERM_PIPE','AGWINTERM_SESSION_ID','AGWINTERM_PANE_ID','WEBVIEW2_BROWSER_EXECUTABLE_FOLDER')) {
    $saved[$key]=[Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key,$null)
}
$process=$null; $samples=[Collections.Generic.List[object]]::new(); $loads=[Collections.Generic.List[object]]::new()
Add-Type -Path "$PSHOME/UIAutomationTypes.dll"
Add-Type -Path "$PSHOME/UIAutomationClient.dll"
Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class WebMeasureWindow {
 delegate bool Visit(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Visit v,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 public static IntPtr Find(int pid){IntPtr result=IntPtr.Zero;EnumWindows((h,p)=>{GetWindowThreadProcessId(h,out uint id);var s=new StringBuilder(100);GetClassNameW(h,s,100);var title=new StringBuilder(100);GetWindowTextW(h,title,100);if(id==pid&&s.ToString()=="AgwintermWin32"&&title.ToString()!="agwinterm quick terminal"){result=h;return false;}return true;},IntPtr.Zero);return result;}
}
'@
function Ctl([string[]]$Words) {
    $raw = & $Ctl @Words --pipe $identity --json 2>$null
    if (-not $raw) { throw "No control reply: $Words" }
    $reply=$raw | ConvertFrom-Json
    if (-not $reply.ok) { throw ($reply | ConvertTo-Json -Compress) }
    return $reply.result
}
function Owned-Processes {
    $all=@(Get-CimInstance Win32_Process); $ids=[Collections.Generic.HashSet[int]]::new(); [void]$ids.Add($process.Id)
    do { $changed=$false; foreach($row in $all) { if($ids.Contains([int]$row.ParentProcessId) -and $ids.Add([int]$row.ProcessId)){$changed=$true} } } while($changed)
    return @($all | Where-Object {$ids.Contains([int]$_.ProcessId)})
}
function Sample([string]$Label) {
    $rows=Owned-Processes; $ids=@($rows.ProcessId)
    $before=@(Get-Process -Id $ids -ErrorAction SilentlyContinue); $cpu0=($before | Measure-Object CPU -Sum).Sum
    $watch=[Diagnostics.Stopwatch]::StartNew(); Start-Sleep -Seconds 1
    $after=@(Get-Process -Id $ids -ErrorAction SilentlyContinue); $seconds=$watch.Elapsed.TotalSeconds
    $gpuMemory=@(); $gpuEngines=@(); $gpuError=$null
    try {
        $pattern='^pid_(' + (($ids | ForEach-Object {[string]$_}) -join '|') + ')_'
        $gpuMemory=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory | Where-Object Name -match $pattern | Select-Object Name,DedicatedUsage,SharedUsage,TotalCommitted)
        $gpuEngines=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine | Where-Object Name -match $pattern | Select-Object Name,UtilizationPercentage)
    } catch { $gpuError=$_.Exception.Message }
    $entry=[pscustomobject]@{ label=$Label; timestampUtc=[DateTime]::UtcNow.ToString('o'); processes=@($rows | Select-Object ProcessId,ParentProcessId,Name,CommandLine); browserCount=@($rows | Where-Object Name -eq 'msedgewebview2.exe').Count; workingSetBytes=($after | Measure-Object WorkingSet64 -Sum).Sum; privateBytes=($after | Measure-Object PrivateMemorySize64 -Sum).Sum; cpuPercentOfOneCore=[Math]::Max(0,((($after | Measure-Object CPU -Sum).Sum-$cpu0)/$seconds*100)); temporaryProfileCount=@(Get-ChildItem "$directory/webview2/EBWebView" -Directory -Filter 'WV2Profile_page-*' -ErrorAction SilentlyContinue).Count; profileBytes=(Get-ChildItem "$directory/webview2" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum; gpuMemory=$gpuMemory; gpuEngines=$gpuEngines; gpuError=$gpuError }
    $samples.Add($entry); Write-Host "SAMPLE $Label browsers=$($entry.browserCount) private=$($entry.privateBytes)"
}
function Load-Page([string]$Owner,[string]$Kind) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    if($Kind -eq 'reload'){ [void](Ctl @('session','overlay','reload','--target',$Owner)) }
    else { [void](Ctl @('session','overlay','open','--html',$source,'--js','--follow','--target',$Owner)) }
    for($i=0;$i -lt 200;$i++) {
        $page=@((Ctl @('tree')).workspaces.sessions | Where-Object id -eq $Owner)[0].htmlOverlays[0]
        if($page.state -eq 'failed'){throw $page.error}
        if($page.state -eq 'loaded'){break}; Start-Sleep -Milliseconds 50
    }
    if($page.state -ne 'loaded'){throw 'page load timed out'}
    $loadedMs=$watch.Elapsed.TotalMilliseconds; $paint=$null
    $root=[System.Windows.Automation.AutomationElement]::FromHandle([WebMeasureWindow]::Find($process.Id))
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Paint timing')
    for($i=0;$i -lt 80;$i++) {
        $input=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if($input){$value=$input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value;if($value){$paint=[double]::Parse($value,[Globalization.CultureInfo]::InvariantCulture);break}}
        Start-Sleep -Milliseconds 50
    }
    $loads.Add([pscustomobject]@{kind=$Kind;pageID=$page.id;requestToLoadedMs=$loadedMs;firstContentfulPaintMs=$paint;paintObservationMs=$watch.Elapsed.TotalMilliseconds})
}
try {
    $process=Start-Process $Exe -ArgumentList '--app-id',$identity,'--pipe',$identity,'--no-restore' -WindowStyle Hidden -PassThru
    for($i=0;$i -lt 100;$i++){try{$tree=Ctl @('tree');break}catch{Start-Sleep -Milliseconds 100}}
    [void][WebMeasureWindow]::ShowWindow([WebMeasureWindow]::Find($process.Id),4)
    $owners=[Collections.Generic.List[string]]::new(); $owners.Add($tree.workspaces[0].sessions[0].id)
    Sample '0-terminal-only'
    foreach($count in @(1,5,10)) {
        while($owners.Count -lt $count){$owners.Add([string](Ctl @('session','new','--no-select')))}
        $from=if($count -eq 1){0}elseif($count -eq 5){1}else{5}
        for($i=$from;$i -lt $count;$i++){Load-Page $owners[$i] 'first-document'}
        Sample "$count-pages-one-visible"
        foreach($owner in $owners){[void](Ctl @('session','overlay','hide','--target',$owner))}
        Sample "$count-pages-all-hidden"
    }
    [void](Ctl @('session','overlay','show','--target',$owners[0])); [void](Ctl @('session','select','--target',$owners[0]))
    1..3 | ForEach-Object { Load-Page $owners[0] 'reload' }
    foreach($owner in $owners){[void](Ctl @('session','overlay','close','--target',$owner))}
    Start-Sleep -Seconds 5; Sample 'all-closed-5s'
    Start-Sleep -Seconds 25; Sample 'all-closed-30s'
    foreach($owner in @($owners | Select-Object -Skip 1)){[void](Ctl @('session','close','--target',$owner))}
    for($cycle=1;$cycle -le 30;$cycle++) {
        Load-Page $owners[0] 'cycle-open'; [void](Ctl @('session','overlay','close','--target',$owners[0]))
        if($cycle%5 -eq 0){Start-Sleep -Seconds 3;Sample "cycle-$cycle-closed"}
    }
    Start-Sleep -Seconds 25; Sample 'cycles-closed-settled'
} finally {
    [pscustomobject]@{timestampUtc=[DateTime]::UtcNow.ToString('o');executable=$Exe;privateDirectory=$directory;protocol='process-tree samples; CPU sampled over one second; GPU per-process counters; browser PerformanceObserver first-contentful-paint via UIA; 30 open/close cycles';loads=$loads.ToArray();samples=$samples.ToArray()} | ConvertTo-Json -Depth 10 | Set-Content $Output
    if($process -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
    foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}
}
