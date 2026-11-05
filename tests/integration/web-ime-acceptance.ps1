param([string]$Exe, [string]$Ctl, [Parameter(Mandatory)][string]$LanguageSource,
    [ValidateSet('Ghostty')][string]$TerminalCore)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WebImeAcceptance -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
if ($env:AGWINTERM_HYPERV_UI -ne '1' -or -not (& "$PSScriptRoot/hyperv-ui-context.ps1")) { throw 'IME setup requires a disposable Hyper-V guest' }
$output=Join-Path $PSScriptRoot '../../artifacts/web-ime-acceptance'
$null=New-Item -ItemType Directory -Force -Path $output
$report=[ordered]@{outcome='FAIL';commit=$env:AGWINTERM_TEST_COMMIT;os=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version);events=@();error=$null}
$process=$null
try {
    $report.executableSha256=(Get-FileHash -LiteralPath $Exe).Hash
    $assembly=Join-Path (Split-Path $Exe) 'Agwinterm.Win32.dll'
    if(Test-Path -LiteralPath $assembly){$report.applicationAssemblySha256=(Get-FileHash -LiteralPath $assembly).Hash}
    $report.cliSha256=(Get-FileHash -LiteralPath $Ctl).Hash
    $report.runtime=@(Get-ChildItem "${env:ProgramFiles(x86)}/Microsoft/EdgeWebView/Application" -Filter msedgewebview2.exe -Recurse | ForEach-Object {$_.VersionInfo.FileVersion})
    $report.network=@(Get-NetAdapter -ErrorAction SilentlyContinue | Select-Object Name,Status)
    if($report.network.Count){throw 'Expected the offline worker guest'}
    foreach($key in @('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER','AGWINTERM_TEST_WEBVIEW2','AGWINTERM_APP_ID','AGWINTERM_PIPE','AGWINTERM_SESSION_ID','AGWINTERM_PANE_ID')){[Environment]::SetEnvironmentVariable($key,$null)}
    $languageDirectory=Join-Path $output 'packages'
    $archive=Join-Path $output 'language.zip'
    Copy-Item -LiteralPath $LanguageSource -Destination $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $languageDirectory
    Remove-Item -LiteralPath $archive
    $packages=@(Get-ChildItem -LiteralPath $languageDirectory -Filter '*.cab' | Sort-Object Name)
    if(-not $packages.Count){throw 'No official language packages supplied'}
    $report.packages=@($packages | ForEach-Object { @{name=$_.Name;sha256=(Get-FileHash $_.FullName).Hash} })
    foreach($package in $packages) {
        & dism.exe /Online /Add-Package "/PackagePath:$($package.FullName)" /NoRestart *> (Join-Path $output ($package.BaseName+'.log'))
        ($report.packages | Where-Object name -eq $package.Name).installExitCode=$LASTEXITCODE
        if($LASTEXITCODE -notin @(0,3010)){throw "Language package installation failed: $LASTEXITCODE"}
    }
    & powershell.exe -NoProfile -Command '$ErrorActionPreference="Stop"; $cap=Get-WindowsCapability -Online -Name "Language.Basic~~~zh-CN~0.0.1.0"; $cap | ConvertTo-Json; if($cap.State.ToString() -ne "Installed"){throw "Chinese basic typing is not installed"}; $list=New-WinUserLanguageList zh-CN; $list.Add("en-US"); Set-WinUserLanguageList $list -Force; Set-WinDefaultInputMethodOverride -InputTip $list[1].InputMethodTips[0]; foreach($language in $list){$language | Select-Object LanguageTag,InputMethodTips | ConvertTo-Json -Depth 5}' *> (Join-Path $output 'language-setup.log')
    if($LASTEXITCODE -ne 0){throw 'Chinese input profile setup failed'}
    $inputServices=@(Get-Service TextInputManagementService,TabletInputService -ErrorAction SilentlyContinue)
    foreach($service in $inputServices){
        if($service.StartType -eq 'Disabled'){Set-Service -Name $service.Name -StartupType Manual}
        if($service.Status -ne 'Running'){Start-Service -Name $service.Name}
    }
    $report.inputServices=@($inputServices | ForEach-Object {Get-Service -Name $_.Name} | Select-Object Name,Status,StartType)
    if(-not(Get-Process ctfmon -ErrorAction SilentlyContinue)){$inputProcess=Start-Process "$env:windir/System32/ctfmon.exe" -WindowStyle Hidden -PassThru}
    Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ImeKeys {
 delegate bool Visitor(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Visitor v,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int n);
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 public static void Click(int x,int y) { SetCursorPos(x,y);System.Threading.Thread.Sleep(100);mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero); }
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint MapVirtualKey(uint key,uint type);
 [StructLayout(LayoutKind.Explicit,Size=40)] struct Input { [FieldOffset(0)]public uint Type;[FieldOffset(8)]public ushort Key;[FieldOffset(10)]public ushort Scan;[FieldOffset(12)]public uint Flags;[FieldOffset(16)]public uint Time;[FieldOffset(24)]public UIntPtr Extra; }
 [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int size);
 static void Send(byte key,bool up) { uint scan=MapVirtualKey(key,4);var input=new Input{Type=1,Scan=(ushort)(scan&0xff),Flags=8u|(up?2u:0u)|((scan&0xff00)!=0?1u:0u)};if(SendInput(1,new[]{input},Marshal.SizeOf<Input>())!=1)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
 [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
 [StructLayout(LayoutKind.Sequential)] struct GuiInfo { public int Size,Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint id,ref GuiInfo info);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int size,bool repaint);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 public static IntPtr Find(int pid) { IntPtr found=IntPtr.Zero; EnumWindows((h,p)=>{GetWindowThreadProcessId(h,out uint id);var s=new StringBuilder(256);GetClassName(h,s,256);var title=new StringBuilder(256);GetWindowText(h,title,256);if(id==pid && s.ToString()=="AgwintermWin32" && title.ToString()!="agwinterm quick terminal"){found=h;return false;}return true;},IntPtr.Zero);return found; }
 public static void Focus(IntPtr h) { SetForegroundWindow(h);for(int i=0;i<50 && GetForegroundWindow()!=h;i++)System.Threading.Thread.Sleep(20);if(GetForegroundWindow()!=h)throw new Exception("Owned IME window is not foreground"); }
 public static void Key(byte k) { Send(k,false);System.Threading.Thread.Sleep(40);Send(k,true);System.Threading.Thread.Sleep(80); }
 public static void ShiftTab() { Send(0xa0,false);try{Key(0x09);}finally{Send(0xa0,true);} }
 public static void Palette() { Key(0x79); }
 public static void Chinese(IntPtr h) {
   for(int i=0;i<4 && (Layout(h)&0xffff)!=0x0804;i++) {
     Send(0x5b,false);try{Key(0x20);}finally{Send(0x5b,true);}
     System.Threading.Thread.Sleep(500);
   }
   if((Layout(h)&0xffff)!=0x0804)throw new Exception("Win+Space did not select the installed Chinese input profile");
 }
 public static long Layout(IntPtr h) { var info=new GuiInfo{Size=Marshal.SizeOf<GuiInfo>()};if(GetGUIThreadInfo(0,ref info) && info.Focus!=IntPtr.Zero)h=info.Focus;return GetKeyboardLayout(GetWindowThreadProcessId(h,out _)).ToInt64(); }

}
'@
    Add-Type -Path "$PSHOME/UIAutomationTypes.dll"
    Add-Type -Path "$PSHOME/UIAutomationClient.dll"
    $providers=[Reflection.Assembly]::LoadFrom("$PSHOME/UIAutomationClientSideProviders.dll")
    $table=$providers.GetExportedTypes()[0].GetField('ClientSideProviderDescriptionTable').GetValue($null)
    try{[System.Windows.Automation.ClientSettings]::RegisterClientSideProviders($table)}catch{[System.Windows.Automation.ClientSettings]::RegisterClientSideProviders($table)}
    $identity='agwinterm-ime-'+[guid]::NewGuid().ToString('N')
    $data=Join-Path $env:LOCALAPPDATA $identity
    $null=New-Item -ItemType Directory -Path $data
    if ($TerminalCore) { "emulator-core = $($TerminalCore.ToLowerInvariant())" | Set-Content (Join-Path $data 'agwinterm.conf') }
    @{default='Test';profiles=@(@{name='Test';command='cmd.exe';args=@('/d','/q','/k','echo AW_IME_READY');cwd=($env:SystemDrive+'\')})} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $data 'profiles.json')
    'map f10 = action_palette' | Set-Content (Join-Path $data 'keymap.conf')
    $source=Join-Path $data 'ime.html'
    @'
<!doctype html><meta charset="utf-8"><title>Real IME acceptance</title>
<textarea aria-label="IME editor" rows="12" cols="60" autofocus></textarea><button>Focus target</button>
<textarea aria-label="IME events" readonly></textarea>
<script>const editor=document.querySelector('textarea'),log=document.querySelectorAll('textarea')[1],events=[];for(const type of ['compositionstart','compositionupdate','compositionend','input'])editor.addEventListener(type,e=>{events.push({type,data:e.data,value:editor.value,isComposing:e.isComposing});log.value=JSON.stringify(events)});</script>
'@ | Set-Content $source -Encoding utf8
    function Call([string[]]$Words){$raw=& $Ctl @Words --pipe $identity --timeout 3 --json 2>$null;if(-not $raw){throw "No IME CLI reply: $Words"};$raw | ConvertFrom-Json}
    function Require([bool]$ok,[string]$message){if(-not $ok){throw $message};Write-Host "PASS $message"}
    $process=Start-Process $Exe -ArgumentList @('--app-id',$identity,'--pipe',$identity,'--no-restore') -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do{try{if((Call @('session','text')).result -match 'AW_IME_READY'){break}}catch{};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)
    Start-Sleep -Milliseconds 500
    Require ((Call @('session','text')).result -match 'AW_IME_READY') 'published terminal is ready before IME input'
    $owner=(Call @('tree')).result.workspaces[0].sessions[0].id
    $before=(Call @('session','text','--target',$owner)).result
    Require ((Call @('session','overlay','open','--html',$source,'--js','--navigation','--target',$owner)).ok) 'real IME page opens'
    for($i=0;$i -lt 100;$i++){$page=(Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0];if($page.state -ne 'loading'){break};Start-Sleep -Milliseconds 100}
    $report.page=$page
    Require ($page.state -eq 'loaded') 'IME page finishes loading'
    $handle=[ImeKeys]::Find($process.Id)
    [void][ImeKeys]::ShowWindow($handle,4)
    [ImeKeys]::Focus($handle)
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
    function Element([string]$label){$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$label))}
    for($i=0;$i -lt 100;$i++){$editor=Element 'IME editor';if($editor){break};Start-Sleep -Milliseconds 100}
    Require ($null -ne $editor) 'IME editor is accessible'
    $editor.SetFocus()
    $editorRect=$editor.Current.BoundingRectangle
    [ImeKeys]::Click([int]($editorRect.Left+20),[int]($editorRect.Top+20))
    [ImeKeys]::Chinese($handle)
    $report.inputProcesses=@(Get-CimInstance Win32_Process | Where-Object Name -match '^(ctfmon|TextInputHost|ChsIME|ImeBroker)\.exe$' | Select-Object Name,ProcessId,SessionId)
    Start-Sleep -Seconds 2
    $report.layout=[ImeKeys]::Layout($handle)
    $value=$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $log=(Element 'IME events').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $tray=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'Shell_TrayWnd'))
    if($tray){
        $trayItems=@($tray.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition))
        $report.inputTray=@($trayItems | ForEach-Object {$_.Current.Name} | Where-Object {$_})
    }
    # Pinyin can own composition in its native candidate window and deliver
    # committed input without DOM composition events. No Unicode is injected.
    foreach($key in 'NIHAO'.ToCharArray()){[ImeKeys]::Key([byte][char]$key)}
    Start-Sleep -Milliseconds 500
    $capture=[Drawing.Bitmap]::new([ImeKeys]::GetSystemMetrics(0),[ImeKeys]::GetSystemMetrics(1))
    $graphics=[Drawing.Graphics]::FromImage($capture)
    try{$graphics.CopyFromScreen(0,0,0,0,$capture.Size);$capture.Save([IO.Path]::GetFullPath((Join-Path $output 'candidates.png')))}finally{$graphics.Dispose();$capture.Dispose()}
    $report.beforeCandidate=$value.Current.Value
    [ImeKeys]::Key(0x32)
    for($poll=0;$poll -lt 30;$poll++){
        Start-Sleep -Milliseconds 100
        $committed=$value.Current.Value
        if($committed -ceq '你号'){break}
    }
    $report.committed=$committed
    $report.candidateEvents=@($log.Current.Value | ConvertFrom-Json)
    Require ($committed -ceq '你号') 'native Pinyin candidate 2 commits Chinese text exactly once'
    foreach($key in 'ZHONG'.ToCharArray()){[ImeKeys]::Key([byte][char]$key)}
    [ImeKeys]::Key(0x1b)
    Start-Sleep -Milliseconds 500
    Require ($value.Current.Value -ceq $committed) 'Escape cancels composition without inserting text'
    function Compose-Again([string]$label) {
        $baseline=$value.Current.Value
        $previous=@($log.Current.Value | ConvertFrom-Json).Count
        foreach($key in 'NIHAO'.ToCharArray()){[ImeKeys]::Key([byte][char]$key)}
        Start-Sleep -Milliseconds 500
        $capture=[Drawing.Bitmap]::new([ImeKeys]::GetSystemMetrics(0),[ImeKeys]::GetSystemMetrics(1))
        $graphics=[Drawing.Graphics]::FromImage($capture)
        try{$graphics.CopyFromScreen(0,0,0,0,$capture.Size);$capture.Save([IO.Path]::GetFullPath((Join-Path $output ("candidates-"+$label.Replace(' ','-')+'.png'))))}finally{$graphics.Dispose();$capture.Dispose()}
        [ImeKeys]::Key(0x31)
        for($poll=0;$poll -lt 30;$poll++){
            Start-Sleep -Milliseconds 100
            if($value.Current.Value -ceq ($baseline+'你好') -or $value.Current.Value -ceq ($baseline+'你号')){break}
        }
        $events=@($log.Current.Value | ConvertFrom-Json) | Select-Object -Skip $previous
        $actual=$value.Current.Value
        $report["composition-$label"]=@{baseline=$baseline;actual=$actual;events=@($events)}
        # Selecting candidate 2 earlier can train Pinyin to rank that word first.
        Require ($actual -ceq ($baseline+'你好') -or $actual -ceq ($baseline+'你号')) "${label}: native Pinyin commits exactly once"
    }
    # Verify the shortcut with no preedit. During active preedit Windows Pinyin
    # owns candidate keys, so exercise the native menu to open the palette.
    [ImeKeys]::Palette()
    for($poll=0;$poll -lt 50;$poll++){
        if(-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible){break}
        Start-Sleep -Milliseconds 100
    }
    Require (-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible) 'native palette shortcut works with Chinese IME selected'
    [ImeKeys]::Key(0x1b)
    Start-Sleep -Milliseconds 500
    Require $editor.Current.HasKeyboardFocus 'shortcut dismissal restores editor focus'
    foreach($key in 'NI'.ToCharArray()){[ImeKeys]::Key([byte][char]$key)}
    ((Element 'Navigate').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    for($poll=0;$poll -lt 50;$poll++){$paletteItem=Element 'Command Palette';if($paletteItem){break};Start-Sleep -Milliseconds 100}
    Require ($null -ne $paletteItem) 'native palette menu remains available during composition'
    ($paletteItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    for($poll=0;$poll -lt 50;$poll++){
        if(-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible){break}
        Start-Sleep -Milliseconds 100
    }
    $report.afterPaletteValue=$value.Current.Value
    Require (-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible) 'native palette opens during IME composition'
    [ImeKeys]::Key(0x1b)
    Start-Sleep -Milliseconds 500
    Require $editor.Current.HasKeyboardFocus 'palette dismissal restores editor focus'
    [ImeKeys]::Key(0x1b)
    Compose-Again 'After palette'
    [void][ImeKeys]::MoveWindow($handle,40,40,1000,720,$true)
    Start-Sleep -Milliseconds 300
    Compose-Again 'After resize'
    $peer=Call @('session','new','--name','ime-peer')
    Require $peer.ok 'second IME test session opens'
    $null=Call @('session','select','--target',$owner)
    Start-Sleep -Milliseconds 500
    Require $editor.Current.HasKeyboardFocus 'returning to the session restores editor focus'
    Compose-Again 'After session switch'
    [ImeKeys]::Key(0x09)
    Start-Sleep -Milliseconds 200
    Require (-not $editor.Current.HasKeyboardFocus) 'Tab leaves the editor'
    [ImeKeys]::ShiftTab()
    Start-Sleep -Milliseconds 200
    Require $editor.Current.HasKeyboardFocus 'Shift+Tab returns to the editor'
    $report.sessionEvents=$log.Current.Value | ConvertFrom-Json
    $null=Call @('session','overlay','close','--target',$owner)
    Require ((Call @('session','text','--target',$owner)).result -ceq $before) 'session-wide IME input never reaches the terminal'
    Require ((Call @('session','split','on','--target',$owner)).ok) 'split session opens for pane IME acceptance'
    Start-Sleep -Milliseconds 500
    $paneIds=@((Call @('tree')).result.workspaces[0].sessions[0].paneIds)
    Require ($paneIds.Count -eq 2) 'split exposes exact pane identities'
    $leftBefore=(Call @('session','text','--target',$paneIds[0])).result
    $rightBefore=(Call @('session','text','--target',$paneIds[1])).result
    Require ((Call @('session','overlay','open','--html',$source,'--js','--navigation','--pane','left','--target',$owner)).ok) 'pane-scoped IME page opens'
    $editor=$null
    for($i=0;$i -lt 100;$i++){$editor=Element 'IME editor';if($editor){break};Start-Sleep -Milliseconds 100}
    Require ($null -ne $editor) 'pane editor is accessible'
    $editor.SetFocus()
    $rect=$editor.Current.BoundingRectangle
    [ImeKeys]::Click([int]($rect.Left+20),[int]($rect.Top+20))
    Start-Sleep -Milliseconds 500
    $value=$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $log=(Element 'IME events').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    Compose-Again 'Pane-scoped page'
    $paneCommitted=$value.Current.Value
    foreach($key in 'ZHONG'.ToCharArray()){[ImeKeys]::Key([byte][char]$key)}
    [ImeKeys]::Key(0x1b)
    Start-Sleep -Milliseconds 500
    Require ($value.Current.Value -ceq $paneCommitted) 'pane composition cancels without inserting text'
    $report.events=$log.Current.Value | ConvertFrom-Json
    $null=Call @('session','overlay','close','--pane','left','--target',$owner)
    Require ((Call @('session','text','--target',$paneIds[0])).result -ceq $leftBefore) 'pane IME input does not reach its terminal'
    Require ((Call @('session','text','--target',$paneIds[1])).result -ceq $rightBefore) 'pane IME input does not reach the other pane'
    if ($TerminalCore) { . "$PSScriptRoot/ghostty-ime-ui-cases.ps1" }
    $report.outcome='PASS'
} catch { $report.error=$_.ToString();throw }
finally {
    if($root){
        try{$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { "$($_.Current.ControlType.ProgrammaticName) $($_.Current.Name)" } | Set-Content (Join-Path $output 'ui-tree.txt')}catch{}
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'ime.json')
    if($process -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
    if($inputProcess -and -not $inputProcess.HasExited){$inputProcess.Kill();$inputProcess.WaitForExit()}
}
