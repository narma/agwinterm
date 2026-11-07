# Runs only inside hud-ui's routed, owned VM/desktop fixture.
Add-Type -Path "$PSHOME/UIAutomationTypes.dll"
Add-Type -Path "$PSHOME/UIAutomationClient.dll"
Add-Type -TypeDefinition @'
using System;using System.Collections.Generic;using System.Runtime.InteropServices;using System.Text;
public static class AgentTestWindows {
 delegate bool Visitor(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Visitor f,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,StringBuilder b,int n);
 public static IntPtr Popup(uint pid){IntPtr found=IntPtr.Zero;EnumWindows((h,p)=>{uint n;GetWindowThreadProcessId(h,out n);if(n==pid){var cls=new StringBuilder(256);GetClassNameW(h,cls,256);if(cls.ToString()=="agwinterm-menu")found=h;}return true;},IntPtr.Zero);return found;}
}
'@
function AgentWait([scriptblock]$condition, [int]$attempts=70){for($i=0;$i-lt $attempts;$i++){if(& $condition){return $true};Start-Sleep -Milliseconds 100};return $false}
function WaitAgentShell([string]$id){
    if(-not (AgentWait {try{([string](Rpc 'session.text' @{} $id)).Contains('>')}catch{$false}})){
        throw "Agent fixture shell did not become ready: $id"
    }
}
function AgentElements {
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object {$_.Current.Name -like 'Agent *' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button})
}
function FindAgent([string]$name){AgentElements|Where-Object {$_.Current.Name -like "Agent $name,*"}|Select-Object -First 1}
function InvokeElement($element){if($null-eq $element){throw 'Missing UIA element'};($element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}
function InvokeNamed([string]$name){
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
    InvokeElement ($root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition))
}
function AgentMenuChoice([string]$label){
    if(-not (AgentWait {[AgentTestWindows]::Popup($job.Pid)-ne [IntPtr]::Zero})){throw 'Agent menu did not open'}
    # Context menus use the existing keyboard routing on the owner HWND.
    $row=switch($label){'Pin agent'{1};'Unpin agent'{1};'Grouped by workspace'{1};default{throw "Unknown menu fixture choice $label"}}
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x24,[IntPtr]1)
    for($i=0;$i-lt $row;$i++){[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x28,[IntPtr]1)}
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x0d,[IntPtr]1)
}
function AgentContext($element){
    $rect=$element.Current.BoundingRectangle
    $x=[int]($rect.Left+$rect.Width/2);$y=[int]($rect.Top+$rect.Height/2)
    [void][HudOwnedJob]::SendMessageW($hwnd,0x7b,$hwnd,[IntPtr](($y-shl 16)-bor ($x-band 65535)))
}
function ReportAgent([string]$target,[string]$payload){$null=Rpc 'session.write' @{text=([string][char]27+']7501;'+$payload+[char]7)} $target}

$agentWindow=[string](@((Rpc 'window.list').windows|Where-Object open)[0].id)
$agentStatePath=Join-Path $appDir ('windows/'+$agentWindow+'.json')
# Keep another terminal selected, so completion really is unseen.
$worker=[string](Rpc 'session.new' @{name='Agent worker';'no-select'=$true})
if(-not (AgentWait {$null-ne (Node $worker)})){throw 'Worker session was not published'}
WaitAgentShell $worker
$null=Rpc 'session.select' @{} $session
Check 'ordinary untouched shells absent from agents' (@(AgentElements).Count-eq 0)
ReportAgent $worker 'state=working:app=codex:title=V29ya2Vy'
Check 'OSC-only worker appears as Working' (AgentWait {($a=FindAgent 'Worker') -and $a.Current.Name -like '*, Working,*'})
ReportAgent $worker 'state=blocked:kind=permission:app=codex:title=V29ya2Vy'
Check 'OSC blocked appears' (AgentWait {($a=FindAgent 'Worker') -and $a.Current.Name -like '*, Blocked,*'})
InvokeElement (FindAgent 'Worker')
Check 'agent row selects its terminal' (AgentWait {(Node $worker).active})
Check 'Blocked survives viewing' ((FindAgent 'Worker').Current.Name -like '*, Blocked,*')
$null=Rpc 'session.select' @{} $session
ReportAgent $worker 'state=working:app=codex:title=V29ya2Vy'
ReportAgent $worker 'state=done:app=codex:title=V29ya2Vy'
ReportAgent $worker 'state=idle:app=codex:title=V29ya2Vy'
Check 'Done survives immediate idle report' (AgentWait {($a=FindAgent 'Worker') -and $a.Current.Name -like '*, Done,*'})
InvokeElement (FindAgent 'Worker')
Check 'viewing Done acknowledges result' (AgentWait {($a=FindAgent 'Worker') -and $a.Current.Name -like '*, Idle,*'})
ReportAgent $worker 'state=blocked:id=tests:title=VGVzdHM=:kind=question'
Check 'child record appears' (AgentWait {$null-ne (FindAgent 'Tests')})
Check 'blocked child raises parent priority' ((FindAgent 'Worker').Current.Name -like '*, Blocked,*')
AgentContext (FindAgent 'Worker'); AgentMenuChoice 'Pin agent'
# Owner mode exports terminal history before publishing the window snapshot.
# Give disk persistence a longer deadline than interactive UI responses.
Check 'pin persisted separately from flags' (AgentWait {
    $state=Get-Content $agentStatePath -Raw -ErrorAction SilentlyContinue|ConvertFrom-Json
    @($state.AgentPins).Count-eq 1
} 400)
InvokeNamed 'Sort agents'; AgentMenuChoice 'Grouped by workspace'
Check 'sort preference persisted' (AgentWait {
    (Get-Content $agentStatePath -Raw|ConvertFrom-Json).AgentSort-eq 'grouped'
} 400)
$null=Shot 'agents-grouped-pinned'
AgentContext (FindAgent 'Worker'); AgentMenuChoice 'Unpin agent'
Check 'unpin persisted' (AgentWait {
    -not (Get-Content $agentStatePath -Raw|ConvertFrom-Json).AgentPins
} 400)
InvokeNamed 'Collapse agents'
Check 'collapsed panel hides rows' (AgentWait {@(AgentElements).Count-eq 0})
InvokeNamed 'Expand agents'
Check 'expanded panel restores rows' (AgentWait {$null-ne (FindAgent 'Worker')})
ReportAgent $worker 'state=clear:id=tests'
Check 'clear removes child record' (AgentWait {$null-eq (FindAgent 'Tests')})
$null=Rpc 'session.select' @{} $session
$null=Rpc 'session.status' @{status='active'} $worker
$null=Shot 'agents-final'

# Hook/API-only agents and background workspaces share this list.
$otherWs=[string](Rpc 'workspace.new' @{name='Other space'})
$hook=[string](Rpc 'session.new' @{name='Hook worker';workspace=$otherWs;'no-select'=$true})
if(-not (AgentWait {$null-ne (Node $hook)})){throw 'Hook session was not published'}
WaitAgentShell $hook
$null=Rpc 'session.status' @{status='blocked';'auto-reset'=$true} $hook
Check 'hook agent from another workspace appears' (AgentWait {$null-ne (FindAgent 'Hook worker')})
InvokeElement (FindAgent 'Hook worker')
Check 'selecting auto-reset Blocked does not clear request' (AgentWait {(Node $hook).status-eq 'blocked'})
$null=Rpc 'session.select' @{} $session
$null=Rpc 'session.status' @{status='active'} $hook
$null=Rpc 'session.status' @{status='completed';'auto-reset'=$true} $hook
Check 'hook completion is unread' (AgentWait {($a=FindAgent 'Hook worker') -and $a.Current.Name -like '*, Done,*'})

# Keyboard traversal must scroll the panel rather than send input to the shell.
for($n=0;$n-lt 25;$n++){ReportAgent $worker ('state=idle:id=worker'+$n.ToString('D2'))}
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x75,[IntPtr]1) # F6
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]9,[IntPtr]1) # Tab to agents
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x23,[IntPtr]1) # End
Check 'keyboard End reveals final agent below fold' (AgentWait {$null-ne (FindAgent 'worker24')})
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x50,[IntPtr]1) # P
Check 'keyboard pin persists the focused child' (AgentWait {
    @((Get-Content $agentStatePath -Raw|ConvertFrom-Json).AgentPins|Where-Object {$_-like '*/worker24'}).Count-eq 1
} 400)
Check 'keyboard pin keeps the moved row visible' (AgentWait {$null-ne (FindAgent 'worker24')})
$beforeOpen=[string](Rpc 'session.text' @{} $worker)
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]13,[IntPtr]1)
[void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr]13,[IntPtr]1)
Check 'keyboard activation selects agent terminal' (AgentWait {(Node $worker).active})
Start-Sleep -Milliseconds 200
Check 'activation Enter is not sent to the shell' (([string](Rpc 'session.text' @{} $worker))-eq $beforeOpen)
$null=Shot 'agents-keyboard-scroll'

$clearProbe=[string](Rpc 'session.new' @{name='OSC cleared';'no-select'=$true})
if(-not (AgentWait {$null-ne (Node $clearProbe)})){throw 'Clear-probe session was not published'}
WaitAgentShell $clearProbe
ReportAgent $clearProbe 'state=blocked'
# Return the scroll to the start before inspecting the new attention row.
InvokeNamed 'Sort agents'
if(-not (AgentWait {[AgentTestWindows]::Popup($job.Pid)-ne [IntPtr]::Zero})){throw 'Sort menu did not open'}
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x24,[IntPtr]1)
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]13,[IntPtr]1)
Check 'new OSC-only agent appears after sorting' (AgentWait {$null-ne (FindAgent 'OSC cleared')})
ReportAgent $clearProbe 'state=clear'
Check 'OSC clear does not leave a synthetic hook agent' (AgentWait {$null-eq (FindAgent 'OSC cleared')})
ReportAgent $clearProbe 'state=working'
ReportAgent $clearProbe 'state=done'
Check 'fresh completion is unread before hidden-sidebar visit' (AgentWait {($a=FindAgent 'OSC cleared') -and $a.Current.Name -like '*, Done,*'})
$null=Rpc 'sidebar' @{op='hide'}
$null=Rpc 'session.select' @{} $clearProbe
if(-not (AgentWait {(Node $clearProbe).active})){throw 'Hidden-sidebar visit did not select session'}
Start-Sleep -Milliseconds 300
$null=Rpc 'session.select' @{} $session
$null=Rpc 'sidebar' @{op='show'}
Check 'hidden-sidebar visit acknowledges completion' (AgentWait {($a=FindAgent 'OSC cleared') -and $a.Current.Name -like '*, Idle,*'})
