param(
    [string]$Exe = "$PSScriptRoot/../../src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
    [string]$Ctl = "$PSScriptRoot/../../src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe",
    [switch]$Cookbook,
    [switch]$CookbookOnly,
    [switch]$NavigationOnly
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$Ctl = (Resolve-Path $Ctl).Path
$identity = 'agwinterm-web-test-' + [guid]::NewGuid().ToString('N')
$directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $identity
New-Item -ItemType Directory -Path $directory | Out-Null
@{ default='Test'; profiles=@(@{name='Test'; command='cmd.exe'; args=@('/d','/q'); cwd=$PSScriptRoot}) } | ConvertTo-Json -Depth 5 | Set-Content "$directory/profiles.json"
"session-host = in-process`nemulator-core = managed" | Set-Content "$directory/agwinterm.conf"
'map f10 = action_palette' | Set-Content "$directory/keymap.conf"
$source = Join-Path $directory 'page.html'
'<!doctype html><meta charset="utf-8"><title>Local test</title><textarea aria-label="Web gate editor">draft</textarea><input aria-label="Theme background" readonly><script>setInterval(()=>document.querySelector("input").value=getComputedStyle(document.documentElement).getPropertyValue("--agterm-background"),50)</script>' | Set-Content $source
$saved = @{}
foreach ($key in @('AGWINTERM_APP_ID','AGWINTERM_PIPE','AGWINTERM_SESSION_ID','AGWINTERM_PANE_ID','WEBVIEW2_BROWSER_EXECUTABLE_FOLDER')) {
    $saved[$key]=[Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key,$null)
}
$env:AGWINTERM_APP_ID = $identity + '-fallback'
$process = $null
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WebGateWindow {
    private delegate bool Visitor(IntPtr hwnd, IntPtr arg);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr arg);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Visitor visitor, IntPtr arg);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(long point);
    public static void ControlClick(IntPtr hwnd, int x, int y) {
        SetForegroundWindow(hwnd);
        for(int i=0;i<50 && GetForegroundWindow()!=hwnd;i++) System.Threading.Thread.Sleep(20);
        if(GetForegroundWindow()!=hwnd) throw new Exception("Test window cannot take foreground for link click");
        keybd_event(0x11,0,0,UIntPtr.Zero);
        try {
            System.Threading.Thread.Sleep(50); SetCursorPos(x-1,y); SetCursorPos(x,y);
            System.Threading.Thread.Sleep(100);
            if(GetForegroundWindow()!=hwnd) throw new Exception("Test window lost foreground before link click");
            var at=WindowFromPoint(((long)y<<32)|(uint)x);
            var cls=new StringBuilder(256);GetClassNameW(at,cls,256);
            Console.WriteLine("Link click at "+x+","+y+" window="+at+" class="+cls+" expected="+hwnd);
            mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero);
        } finally { keybd_event(0x11,0,2,UIntPtr.Zero); }
    }
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    public static void Dpi(IntPtr hwnd,int dpi) {
        GetWindowRect(hwnd,out var rect);
        IntPtr memory=Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
        try { Marshal.StructureToPtr(rect,memory,false); SendMessageW(hwnd,0x2e0,(IntPtr)(dpi|(dpi<<16)),memory); }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public static void Key(IntPtr hwnd,int key) { PostMessageW(hwnd,0x100,(IntPtr)key,(IntPtr)1);PostMessageW(hwnd,0x101,(IntPtr)key,(IntPtr)1); }
    public static IntPtr Find(int pid) {
        IntPtr result=IntPtr.Zero;
        EnumWindows((hwnd,arg)=> { GetWindowThreadProcessId(hwnd,out uint owner); var name=new StringBuilder(256); GetClassNameW(hwnd,name,256); var title=new StringBuilder(256); GetWindowTextW(hwnd,title,256);
            if(owner==pid && name.ToString()=="AgwintermWin32" && title.ToString()!="agwinterm quick terminal") {result=hwnd;return false;} return true; },IntPtr.Zero);
        return result;
    }
    public static void Dump(IntPtr parent) {
        EnumChildWindows(parent,(hwnd,arg)=>{var name=new StringBuilder(256);GetClassNameW(hwnd,name,256);Console.WriteLine("HWND "+hwnd+" "+name+" visible="+IsWindowVisible(hwnd));return true;},IntPtr.Zero);
    }
    public static IntPtr Renderer(IntPtr parent) {
        IntPtr result=IntPtr.Zero;
        EnumChildWindows(parent,(hwnd,arg)=>{var name=new StringBuilder(256);GetClassNameW(hwnd,name,256);if(name.ToString()=="Chrome_RenderWidgetHostHWND" && IsWindowVisible(hwnd)) result=hwnd;return true;},IntPtr.Zero);
        return result;
    }
    public static IntPtr Browser(IntPtr parent) {
        IntPtr result=IntPtr.Zero;
        EnumChildWindows(parent,(hwnd,arg)=>{var name=new StringBuilder(256);GetClassNameW(hwnd,name,256);if(name.ToString()=="Chrome_WidgetWin_1" && IsWindowVisible(hwnd)) result=hwnd;return true;},IntPtr.Zero);
        return result;
    }
    public static void Character(IntPtr hwnd,char value) { PostMessageW(hwnd,0x102,(IntPtr)value,(IntPtr)1); }
}
'@
function Call([string[]]$Words) {
    $text = & $Ctl @Words --pipe $identity --json 2>$null
    if (-not $text) { throw "No response: $Words" }
    return ($text | ConvertFrom-Json)
}
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}
function Start-App([switch]$Restore) {
    $launchArguments = @('--app-id',$identity,'--pipe',$identity)
    if (-not $Restore) { $launchArguments += '--no-restore' }
    $script:process = Start-Process $Exe -ArgumentList $launchArguments -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 100
        try { if ((Call @('ping')).ok) { return } } catch { }
    }
    throw 'Test application did not start'
}
function Stop-App {
    [void](Call @('window','close'))
    if (-not $script:process.WaitForExit(10000)) { throw 'Test app did not close' }
    $script:process=$null
}
try {
    Start-App
    $modules = @($process.Modules | ForEach-Object ModuleName)
    Require (-not ($modules -match 'WebView2')) 'terminal-only startup has no loaded WebView2 modules'
    $children = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $process.Id -and $_.Name -match 'msedge|webview|chrome' })
    Require ($children.Count -eq 0) 'terminal-only startup creates no browser process'
    $owner = (Call @('tree')).result.workspaces[0].sessions[0].id
    Add-Type -Path "$PSHOME/UIAutomationTypes.dll"
    Add-Type -Path "$PSHOME/UIAutomationClient.dll"
    $providers = [System.Reflection.Assembly]::LoadFrom("$PSHOME/UIAutomationClientSideProviders.dll")
    $providerTable = $providers.GetExportedTypes()[0].GetField('ClientSideProviderDescriptionTable').GetValue($null)
    try { [System.Windows.Automation.ClientSettings]::RegisterClientSideProviders($providerTable) }
    catch {
        # PowerShell is not a WPF application: default proxy discovery can fail once.
        if ($_.Exception.InnerException -isnot [NullReferenceException]) { throw }
        [System.Windows.Automation.ClientSettings]::RegisterClientSideProviders($providerTable)
    }
    if ($CookbookOnly -or $NavigationOnly) {
        [void][WebGateWindow]::ShowWindow([WebGateWindow]::Find($process.Id),4)
        . "$PSScriptRoot/cookbook-web-cases.ps1"; Stop-App; return
    }
    Require ((Call @('session','hud','Preparing web','--target',$owner)).ok) 'HUD opens before source validation'
    $invalidSource = Call @('session','overlay','open','--html',($source+'.missing'),'--target',$owner)
    Require (-not $invalidSource.ok -and (Call @('tree')).result.workspaces[0].sessions[0].hud) 'invalid web source preserves existing HUD'
    $opened = Call @('session','overlay','open','--html',$source,'--js','--navigation','--target',$owner)
    Require ($opened.ok -and $opened.result.pageID) 'local page open returns page ID'
    for ($i=0; $i -lt 100; $i++) {
        $snapshot = (Call @('tree')).result.workspaces[0].sessions[0]
        $page=$snapshot.htmlOverlays[0]
        if ($page.state -ne 'loading') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($page.state -eq 'loaded') "local page loaded ($($page.error))"
    Require (-not $snapshot.hud) 'successful controller initialization replaces HUD'
    $handle = [WebGateWindow]::Find($process.Id)
    Require ($handle -ne [IntPtr]::Zero) 'owned native window is found'
    [void][WebGateWindow]::ShowWindow($handle,4)
    $uiaRoot = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    foreach ($label in @('Back','Forward','Reload','Open in browser','Close')) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $label)
        $button = $null
        for ($retry=0; $retry -lt 30 -and -not $button; $retry++) {
            $button = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
            if (-not $button) { Start-Sleep -Milliseconds 100 }
        }
        if (-not $button) { [WebGateWindow]::Dump($handle) }
        Require ($null -ne $button) "native navigation button '$label' is accessible"
    }
    $editorCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Web gate editor')
    $editor = $null
    for ($i=0; $i -lt 30 -and -not $editor; $i++) {
        $editor=$uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$editorCondition)
        if (-not $editor) { Start-Sleep -Milliseconds 100 }
    }
    if (-not $editor) {
        Write-Host ($page | ConvertTo-Json -Compress -Depth 5)
        [WebGateWindow]::Dump($handle)
        $uiaRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { Write-Host "UIA $($_.Current.ControlType.ProgrammaticName) $($_.Current.ClassName) $($_.Current.Name)" }
    }
    Require ($null -ne $editor) 'web editor is discoverable through the parent UIA tree'
    $valuePattern = $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue('unsaved web draft')
    for ($i=0; $i -lt 30 -and $valuePattern.Current.Value -ne 'unsaved web draft'; $i++) { Start-Sleep -Milliseconds 50 }
    Require ($valuePattern.Current.Value -eq 'unsaved web draft') 'native accessibility client can edit the web document'
    $editor.SetFocus()
    for ($i=0; $i -lt 30 -and -not $editor.Current.HasKeyboardFocus; $i++) { Start-Sleep -Milliseconds 50 }
    Require ($editor.Current.HasKeyboardFocus) 'web editor receives keyboard focus'
    $renderer=[WebGateWindow]::Renderer($handle)
    Require ($renderer -ne [IntPtr]::Zero) 'owned browser input window exists'
    [WebGateWindow]::Character($renderer,'Ж')
    for ($i=0; $i -lt 30 -and -not $valuePattern.Current.Value.Contains('Ж'); $i++) { Start-Sleep -Milliseconds 50 }
    Require ($valuePattern.Current.Value.Contains('Ж')) 'Unicode character input reaches web editor'
    Require (-not ((Call @('session','text','--target',$owner)).result -match 'Ж')) 'web character input is not duplicated into terminal'
    $valuePattern.SetValue('unsaved web draft')
    for ($i=0; $i -lt 30 -and $valuePattern.Current.Value -ne 'unsaved web draft'; $i++) { Start-Sleep -Milliseconds 50 }
    Require ($page.id -eq $opened.result.pageID -and $snapshot.overlay) 'tree preserves page ID and occupancy'
    $refused=Call @('session','overlay','open','--html',$source,'--target',$owner)
    Require (-not $refused.ok) 'second page refuses an occupied web slot'
    Require (-not (Call @('session','overlay','open','cmd /d /q /k','--target',$owner)).ok) 'program open refuses an occupied web slot'
    [void](Call @('session','overlay','toggle','--target',$owner))
    Require ((Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].hidden) 'web toggle hides the same page'
    [void](Call @('session','overlay','toggle','--target',$owner))
    Require (-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].hidden) 'web toggle shows the same page'
    Require ((Call @('session','overlay','resize','--width','400','--height','300','--target',$owner)).ok) 'web resize accepts independent DIP dimensions'
    Require ((Call @('session','overlay','resize','--full','--target',$owner)).ok) 'web resize restores full geometry'
    Require ($valuePattern.Current.Value -eq 'unsaved web draft') 'web resize preserves unsaved DOM edits'
    Require ((Call @('session','overlay','hide','--target',$owner)).ok) 'page hides'
    Require (-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible) 'hidden page reports no effective visibility'
    Require ((Call @('session','overlay','result','--page',$opened.result.pageID)).result.pageOutcome.outcome -eq 'pending') 'hidden page outcome stays pending'
    Require ((Call @('session','overlay','show','--target',$owner)).ok) 'page shows'
    Require ($valuePattern.Current.Value -eq 'unsaved web draft') 'hide/show preserves unsaved DOM edits'
    $themeCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Theme background')
    $themeValue = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$themeCondition).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $oldBackground = $themeValue.Current.Value
    Require ($oldBackground -match '^#[0-9A-F]{6}$') 'local page receives CSS theme aliases'
    Require ((Call @('theme','set','Tokyo Night')).ok) 'theme changes while web page is open'
    for ($i=0; $i -lt 30 -and $themeValue.Current.Value -eq $oldBackground; $i++) { Start-Sleep -Milliseconds 50 }
    Require ($themeValue.Current.Value -ne $oldBackground) 'CSS theme aliases update without reload'
    Require ($valuePattern.Current.Value -eq 'unsaved web draft') 'theme change preserves unsaved DOM edits'
    $backCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Back')
    $backButton = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$backCondition)
    for ($i=0; $i -lt 30 -and -not $backButton; $i++) {
        Start-Sleep -Milliseconds 100
        $backButton = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$backCondition)
    }
    Require ($null -ne $backButton) 'navigation remains accessible after theme change'
    Require (-not $backButton.Current.IsEnabled) 'Back is disabled without history'
    $reloadCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Reload')
    $reloadButton = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$reloadCondition)
    $reloadButton.SetFocus()
    for ($i=0; $i -lt 30 -and -not $reloadButton.Current.HasKeyboardFocus; $i++) { Start-Sleep -Milliseconds 50 }
    Require ($reloadButton.Current.HasKeyboardFocus) 'native navigation receives keyboard focus'
    [WebGateWindow]::Key([IntPtr]$reloadButton.Current.NativeWindowHandle,9)
    $forwardCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Open in browser')
    $forwardButton = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$forwardCondition)
    for ($i=0; $i -lt 30 -and -not $forwardButton.Current.HasKeyboardFocus; $i++) { Start-Sleep -Milliseconds 50 }
    Require ($forwardButton.Current.HasKeyboardFocus) 'Tab traverses native navigation buttons'
    $editor.SetFocus()
    [WebGateWindow]::Key([WebGateWindow]::Browser($handle),0x79)
    Start-Sleep -Milliseconds 200
    Require (-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible) 'native palette hides browser presentation'
    [WebGateWindow]::Key($handle,0x1b)
    Start-Sleep -Milliseconds 200
    Require ((Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible) 'dismissing palette restores browser presentation'
    for ($i=0; $i -lt 30 -and -not $editor.Current.HasKeyboardFocus; $i++) { Start-Sleep -Milliseconds 50 }
    Require $editor.Current.HasKeyboardFocus 'dismissing palette returns keyboard focus to web editor'
    Require ((Call @('settings')).ok) 'settings opens above web page'
    Require (-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible) 'settings hides browser presentation'
    $linksCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Open links in, System browser')
    $linksSetting = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$linksCondition)
    Require ($null -ne $linksSetting) 'General settings exposes browser-link policy'
    $linksSetting.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 100
    1..3 | ForEach-Object { [WebGateWindow]::Key($handle,0x28) }
    [WebGateWindow]::Key($handle,0x0d)
    Start-Sleep -Milliseconds 100
    Require ((Call @('browser','links')).result -eq 'overlay75') 'Settings updates the same browser-link preference as CLI'
    [void](Call @('browser','links','browser'))
    [WebGateWindow]::Key($handle,0x1b)
    Start-Sleep -Milliseconds 200
    Require ((Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0].visible) 'dismissing settings restores browser presentation'
    Require ($valuePattern.Current.Value -eq 'unsaved web draft') 'native modal panels preserve web edits'
    $second=Call @('session','new','--name','web-peer')
    Require $second.ok 'second owner session opens'
    [void](Call @('session','select','--target',$owner))
    Require ((Call @('session','switch','begin')).ok) 'native switcher opens'
    $ownerSnapshot=@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
    Require (-not $ownerSnapshot.htmlOverlays[0].visible) 'switcher hides browser presentation'
    [void](Call @('session','switch','cancel'))
    $ownerSnapshot=@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
    Require $ownerSnapshot.htmlOverlays[0].visible 'cancelled switch restores browser presentation'
    $asked='{"title":"Web overlay gate","prompt":"Native dialog above a web page"}' | & $Ctl ask open --no-block --pipe $identity --json | ConvertFrom-Json
    Require (-not [string]::IsNullOrWhiteSpace($asked.id)) 'native ask opens while web page exists'
    $ownerSnapshot=@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
    Require (-not $ownerSnapshot.htmlOverlays[0].visible) 'native ask hides browser presentation'
    & $Ctl ask cancel $asked.id --pipe $identity --json | Out-Null
    $ownerSnapshot=@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
    Require $ownerSnapshot.htmlOverlays[0].visible 'closing native ask restores browser presentation'
    [void](Call @('window','resize','active','900','700'))
    $ownerSnapshot=@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
    $bounds=$ownerSnapshot.htmlOverlays[0].geometry
    Require ($bounds.x -ge 0 -and $bounds.y -ge 0 -and $bounds.width -gt 0 -and $bounds.height -gt 0 -and $bounds.x+$bounds.width -le 900 -and $bounds.y+$bounds.height -le 700) 'resized browser stays inside the owner window'
    Require ($valuePattern.Current.Value -eq 'unsaved web draft') 'switching, native ask and resize preserve DOM edits'
    $monitorDpis=@()
    foreach ($monitor in @((Call @('window','monitors')).result)) {
        Require ((Call @('window','move','--monitor',$monitor.id)).ok) "test window moves to $($monitor.id)"
        Start-Sleep -Milliseconds 200
        $geometry=(Call @('window','geometry')).result
        $monitorDpis+=,$geometry.dpi
        Write-Host "MONITOR $($monitor.id) DPI $($geometry.dpi)"
        Require ($valuePattern.Current.Value -eq 'unsaved web draft') 'monitor move preserves DOM edits'
    }
    if (@($monitorDpis | Select-Object -Unique).Count -lt 2) { Write-Host 'UNVERIFIED mixed physical DPI: all available monitors use the same DPI' }
    foreach ($dpi in @(96,192,144)) {
        [WebGateWindow]::Dpi($handle,$dpi)
        $ownerSnapshot=@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
        $bounds=$ownerSnapshot.htmlOverlays[0].geometry
        Require ($bounds.x -ge 0 -and $bounds.y -ge 0 -and $bounds.width -gt 0 -and $bounds.height -gt 0 -and $bounds.x+$bounds.width -le 900 -and $bounds.y+$bounds.height -le 700) "synthetic DPI $dpi keeps web content clipped"
        Require ($valuePattern.Current.Value -eq 'unsaved web draft') "synthetic DPI $dpi preserves DOM edits"
    }
    $browserProcess = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ParentProcessId -eq $process.Id -and $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -notmatch '--type='
    })
    Require ($browserProcess.Count -eq 1) 'fault injection identifies only this test instance browser process'
    Stop-Process -Id $browserProcess[0].ProcessId -Force
    for ($i=0; $i -lt 50; $i++) {
        $failedPage = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
        if ($failedPage.state -eq 'failed') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($failedPage.state -eq 'failed' -and $failedPage.error) 'browser process failure is observable'
    Require ((Call @('session','overlay','reload','--target',$owner)).ok) 'explicit reload retries after browser process failure'
    for ($i=0; $i -lt 150; $i++) {
        $reloadedPage = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
        if ($reloadedPage.state -eq 'loaded') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($reloadedPage.state -eq 'loaded' -and $reloadedPage.id -eq $opened.result.pageID) 'browser recreation preserves page identity and loads again'
    Require ((Call @('session','overlay','close','--target',$owner)).ok) 'page closes'
    Require (-not (Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays) 'close removes page readback'
    Require ((Call @('session','overlay','result','--page',$opened.result.pageID)).result.pageOutcome.outcome -eq 'dismissed') 'host close dismisses the pending page'
    $peer = @((Call @('tree')).result.workspaces.sessions | Where-Object id -ne $owner)[0].id
    $peerPage = Call @('session','overlay','open','--html',$source,'--target',$peer)
    Require $peerPage.ok 'background page opens without selecting its owner'
    Require ((Call @('session','close','--target',$peer)).ok) 'page owner is removed during asynchronous creation'
    Require ((Call @('session','overlay','result','--page',$peerPage.result.pageID)).result.pageOutcome.outcome -eq 'dismissed') 'owner removal leaves a recoverable dismissed result'
    foreach ($mode in @('empty','cancel','denial','subframe','stale-reload')) {
        $scriptText = switch ($mode) {
            'empty' { "agterm.request('session.overlay.submit',{args:{value:''}});" }
            'cancel' { "agwinterm.request('session.overlay.close',{});" }
            'denial' { "chrome.webview.postMessage({id:'forged',token:'stale',method:'session.overlay.submit',options:{args:{value:'attack'}}}); agterm.request('session.type',{target:'other',args:{text:'attack'}}).catch(e=>agterm.request('session.overlay.submit',{args:{value:'denied: '+e.message}}));" }
            'subframe' { "const frame=document.createElement('iframe'); frame.srcdoc='<script>parent.agterm.request('+JSON.stringify('session.overlay.submit')+',{args:{value:'+JSON.stringify('frame attack')+'}})<'+ '/script>'; document.documentElement.append(frame); setTimeout(()=>agterm.request('session.overlay.submit',{args:{value:'frame blocked'}}),300);" }
            'stale-reload' { "const old=sessionStorage.getItem('old-token'); if(old){chrome.webview.postMessage({id:'old',token:old,method:'session.overlay.submit',options:{args:{value:'stale attack'}}}); agterm.request('session.overlay.submit',{args:{value:'fresh document'}});} else chrome.webview.addEventListener('message', e=>{if(e.data.kind==='review-bind'){sessionStorage.setItem('old-token',e.data.token);location.reload();}});" }
        }
        "<!doctype html><meta charset='utf-8'><script>$scriptText</script>" | Set-Content $source
        $review = Call @('session','overlay','open','--html',$source,'--js','--bridge','review','--target',$owner)
        Require ($review.ok -and $review.result.pageID) "review $mode opens"
        for ($i=0; $i -lt 80; $i++) {
            $result=(Call @('session','overlay','result','--page',$review.result.pageID)).result.pageOutcome
            if ($result.outcome -ne 'pending') { break }
            Start-Sleep -Milliseconds 50
        }
        if ($mode -eq 'empty') { Require ($result.outcome -eq 'submitted' -and $result.value -ceq '') 'empty review submission succeeds' }
        elseif ($mode -eq 'cancel') { Require ($result.outcome -eq 'dismissed') 'review close records dismissal' }
        elseif ($mode -eq 'subframe') { Require ($result.outcome -eq 'submitted' -and $result.value -eq 'frame blocked') 'review subframes cannot submit through the parent bridge' }
        elseif ($mode -eq 'stale-reload') { Require ($result.outcome -eq 'submitted' -and $result.value -eq 'fresh document') 'reload invalidates the previous document token' }
        else { Require ($result.outcome -eq 'submitted' -and $result.value.StartsWith('denied:')) 'unknown bridge commands and stale tokens cannot submit attack data' }
        for ($i=0; $i -lt 40; $i++) {
            $snapshot=@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
            if (-not $snapshot.htmlOverlays) { break }
            Start-Sleep -Milliseconds 50
        }
        Require (-not $snapshot.htmlOverlays) 'completed review closes only its page'
    }
    if ($Cookbook) { . "$PSScriptRoot/cookbook-web-cases.ps1" }
    $mainWindow = @((Call @('window','list')).result.windows | Where-Object open)[0].id
    $extraWindow = (Call @('window','new','--name','web-create-close')).result
    $extraOwner = (Call @('tree','--window',$extraWindow)).result.workspaces[0].sessions[0].id
    $wrongWindow = Call @('session','overlay','open','--html',$source,'--target',$owner,'--window',$extraWindow)
    Require (-not $wrongWindow.ok) 'web open refuses an owner outside the explicitly selected window'
    $latePage = Call @('session','overlay','open','--html',$source,'--target',$extraOwner,'--window',$extraWindow)
    Require $latePage.ok 'web page reserves a slot in a second window'
    Require ((Call @('window','close',$extraWindow)).ok) 'second window closes during page creation'
    [void](Call @('window','select',$mainWindow))
    for ($i=0; $i -lt 50; $i++) {
        $lateOutcome = (Call @('session','overlay','result','--page',$latePage.result.pageID,'--window',$mainWindow)).result.pageOutcome
        if ($lateOutcome.outcome -eq 'dismissed') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($lateOutcome.outcome -eq 'dismissed' -and (Call @('ping')).ok) 'late controller cleanup survives owner-window removal while another window remains'
    Require ((Call @('session','overlay','open','--html',$source,'--target',$owner)).ok) 'page opens before application shutdown'
    Stop-App
    Start-App -Restore
    $restoredPages = @((Call @('tree')).result.workspaces.sessions | ForEach-Object { $_.htmlOverlays } | Where-Object { $_ })
    Require ($restoredPages.Count -eq 0) 'normal workspace restore never restores web pages'
    Stop-App
    $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = Join-Path $directory 'missing-runtime'
    Start-App
    $owner = (Call @('tree')).result.workspaces[0].sessions[0].id
    Require ((Call @('session','overlay','open','cmd /d /q /k','--target',$owner)).ok) 'program overlay opens without browser runtime'
    [void](Call @('session','overlay','hide','--target',$owner))
    $before=(Call @('tree')).result.workspaces[0].sessions[0].overlayDetails | ConvertTo-Json -Depth 8 -Compress
    $refused=Call @('session','overlay','open','--html',$source,'--target',$owner)
    Require (-not $refused.ok -and ($refused | ConvertTo-Json -Compress) -match 'Runtime|runtime') 'missing runtime gives actionable refusal'
    $after=(Call @('tree')).result.workspaces[0].sessions[0].overlayDetails | ConvertTo-Json -Depth 8 -Compress
    Require ($before -eq $after) 'missing-runtime request preserves hidden program overlay'
    [void](Call @('session','overlay','close','--target',$owner))
    Stop-App
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) }
    Write-Host "Private evidence directory: $directory"
}
