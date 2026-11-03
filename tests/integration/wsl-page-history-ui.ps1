# Invoked only inside the WSL private-desktop worker while its page probe runs.
param([int]$OwnerProcess, [string]$RequestPath)
$ErrorActionPreference='Stop'
if (-not [WslUiDesktop]::IsPrivate($env:AGWINTERM_WSL_UI_DESKTOP)) { throw 'Page UI must stay on the private non-input desktop' }
Add-Type -Path "$PSHOME/UIAutomationTypes.dll"
Add-Type -Path "$PSHOME/UIAutomationClient.dll"
$providers=[System.Reflection.Assembly]::LoadFrom("$PSHOME/UIAutomationClientSideProviders.dll")
$table=$providers.GetExportedTypes()[0].GetField('ClientSideProviderDescriptionTable').GetValue($null)
try { [System.Windows.Automation.ClientSettings]::RegisterClientSideProviders($table) }
catch {
    if($_.Exception.InnerException -isnot [NullReferenceException]) { throw }
    [System.Windows.Automation.ClientSettings]::RegisterClientSideProviders($table)
}
if (-not ('Stage4HistoryWindow' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public static class Stage4HistoryWindow {
    private delegate bool Visitor(IntPtr hwnd, IntPtr arg);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr arg);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    public static IntPtr[] Find(int process) {
        var result=new List<IntPtr>();
        EnumWindows((hwnd,arg)=>{ GetWindowThreadProcessId(hwnd,out uint owner); var name=new StringBuilder(256); GetClassNameW(hwnd,name,256);
            if(owner==process && name.ToString()=="AgwintermWin32") { ShowWindow(hwnd,4); result.Add(hwnd); } return true; },IntPtr.Zero);
        return result.ToArray();
    }
}
'@
}
$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Prepare page history')
foreach($handle in [Stage4HistoryWindow]::Find($OwnerProcess)) {
    $root=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $button=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    if ($null -eq $button) { continue }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Remove-Item -LiteralPath $RequestPath
    return $true
}
return $false
