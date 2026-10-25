# Runs only inside hud-ui.ps1's private app-data and owned process job.
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class AskProbe {
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int left,top,right,bottom; }
    delegate bool Visit(IntPtr h,IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visit v,IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h,uint c);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,StringBuilder b,int n);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h,int id);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from,IntPtr to,ref Rect r,uint count);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h,uint msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h,uint msg,IntPtr w,string text);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h,uint msg,IntPtr w,StringBuilder text);
    public static bool SetWindowTextW(IntPtr h,string text) { return SendMessageW(h,12,IntPtr.Zero,text)!=IntPtr.Zero; }
    public static string Text(IntPtr h) { var b=new StringBuilder(16385);SendMessageW(h,13,(IntPtr)b.Capacity,b);return b.ToString(); }
    public static void Dpi(IntPtr h,int dpi,int width,int height) {
        GetWindowRect(h,out var r);r.right=r.left+width;r.bottom=r.top+height;
        var p=Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());try{Marshal.StructureToPtr(r,p,false);SendMessageW(h,0x2e0,(IntPtr)(dpi|(dpi<<16)),p);}finally{Marshal.FreeHGlobal(p);}
    }
    public static bool Fits(IntPtr root,IntPtr child) {
        GetClientRect(root,out var bounds);GetWindowRect(child,out var r);MapWindowPoints(IntPtr.Zero,root,ref r,2);
        return r.left>=0 && r.top>=0 && r.right<=bounds.right && r.bottom<=bounds.bottom && r.right>r.left && r.bottom>r.top;
    }
    public static IntPtr Find(IntPtr owner) { IntPtr found=IntPtr.Zero;EnumWindows((h,p)=>{var b=new StringBuilder(100);GetClassNameW(h,b,100);
        if(GetWindow(h,4)==owner && b.ToString()=="Agwinterm.NativeQuestion"){found=h;return false;}return true;},IntPtr.Zero);return found; }
}
'@
function Open-Ask($form=@{}) {
    $script:askId=[string](Rpc 'ask.open' @{form=$form} -NoTarget).id
    $script:ask=[AskProbe]::Find($hwnd)
    [uint32]$ownerPid=0;[void][HudOwnedJob]::GetWindowThreadProcessId($ask,[ref]$ownerPid)
    if($ask-eq [IntPtr]::Zero -or $ownerPid-ne $job.Pid){throw 'No owned ask HWND'}
}
function Ask-Result([string]$id=$askId){(Rpc 'ask.result' @{} $id -Window '').ask}
function Ask-Control([int]$id){[AskProbe]::GetDlgItem($ask,$id)}
function Ask-Click([int]$id){[void][HudOwnedJob]::SendMessageW((Ask-Control $id),0xf5,[IntPtr]::Zero,[IntPtr]::Zero)}
function Ask-Key([int]$key,[int]$id=200){[void][HudOwnedJob]::SendMessageW((Ask-Control $id),0x100,[IntPtr]$key,[IntPtr]1)}
for($i=0;$i-lt 50 -and ([string](Rpc 'session.text' @{} $session))-notmatch '>'; $i++){Start-Sleep -Milliseconds 100}
$text=Rpc 'session.text' @{all=$true} $session
$front=[HudOwnedJob]::GetForegroundWindow()
$form=@{title='C3 structured question';prompt='Native form — Кириллица';fields=@(
    @{id='name';label='Ваше имя';type='text'},
    @{id='note';label='Комментарий';type='multiline'},
    @{id='one';label='Один вариант';type='single';choices=@(@{id='a';label='Первый'},@{id='b';label='Второй'})},
    @{id='many';label='Несколько вариантов';type='multi';allowOther=$true;choices=@(@{id='x';label='Alpha'},@{id='y';label='Beta'})}
);buttons=@(@{id='yes';label='Принять';hotkey='д'},@{id='revise';label='Revise';hotkey='r'})}
Open-Ask $form
Check 'ask defaults preserve foreground' ([HudOwnedJob]::GetForegroundWindow()-eq $front)
Check 'ask begins pending with native edit' ((Ask-Result).result-eq 'pending' -and (Ask-Control 200)-ne [IntPtr]::Zero)
. "$PSScriptRoot/native-dialog-accessibility.ps1"
$editName=[NativeDialogAccess]::Name((Ask-Control 200))
Check 'native ask edit exposes a descriptive accessible name' ($editName-match 'Ваше имя') $editName
$busy=Rpc 'pick.open' @{items=@();allowCustom=$true} -NoTarget -AllowError
Check 'pending ask refuses picker without replacement' (-not $busy.ok -and (Ask-Result).result-eq 'pending')
$busy=Rpc 'ask.open' @{form=@{}} -NoTarget -AllowError
Check 'second ask refuses' (-not $busy.ok)
$busy=Rpc 'settings.open' @{} -AllowError
Check 'ask owns modal input against settings' (-not $busy.ok)
[void][AskProbe]::SetWindowTextW((Ask-Control 200),'Борис')
foreach($letter in ' тест'.ToCharArray()){[void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr][int]$letter,[IntPtr]1)}
Check 'owner input reaches native text not terminal' ([AskProbe]::Text((Ask-Control 200))-match 'тест')
[void][AskProbe]::SetWindowTextW((Ask-Control 200),'Борис')
Ask-Click 4
[void][AskProbe]::SetWindowTextW((Ask-Control 200),'Line one')
[void][HudOwnedJob]::SendMessageW((Ask-Control 200),0xb1,[IntPtr](-1),[IntPtr](-1))
[void][HudOwnedJob]::SendMessageW((Ask-Control 200),0x102,[IntPtr]13,[IntPtr]1)
Check 'multiline Enter inserts newline and leaves form pending' ([AskProbe]::Text((Ask-Control 200))-match "`n" -and (Ask-Result).result-eq 'pending')
Ask-Click 4
[void][HudOwnedJob]::SendMessageW((Ask-Control 200),0x186,[IntPtr]1,[IntPtr]::Zero)
Ask-Click 4
[void][HudOwnedJob]::SendMessageW((Ask-Control 200),0x185,[IntPtr]1,[IntPtr]1)
[void][HudOwnedJob]::SendMessageW((Ask-Control 200),0x185,[IntPtr]1,[IntPtr]0)
[void][AskProbe]::SetWindowTextW((Ask-Control 203),'Другой')
$selectedNames=[NativeDialogAccess]::SelectedNames((Ask-Control 200))
Check 'native multi-choice exposes accessible selection state' ($selectedNames.Count-eq 2)
$otherName=[NativeDialogAccess]::Name((Ask-Control 203))
Check 'other field exposes its own accessible name' ($otherName-eq 'Other text') $otherName
$originalHwnd=$hwnd;try{$hwnd=$ask;$null=Shot 'native-ask-multi'}finally{$hwnd=$originalHwnd}
Ask-Click 3;Ask-Click 3;Ask-Click 3
Check 'back navigation retains Unicode text' ([AskProbe]::Text((Ask-Control 200))-ceq 'Борис')
[void][HudOwnedJob]::SendMessageW((Ask-Control 200),0x106,[IntPtr][int][char]'д',[IntPtr]1)
$answer=Ask-Result
Check 'Cyrillic Alt character submits exact button and typed answers' ($answer.result-eq 'answered' -and $answer.button-eq 'yes' -and $answer.values.name-ceq 'Борис' -and $answer.values.one.selected[0]-eq 'b' -and ($answer.values.many.selected -join ',')-eq 'x,y' -and $answer.values.many.other-ceq 'Другой') ($answer|ConvertTo-Json -Compress -Depth 8)
$null=Rpc 'ask.cancel' @{} $askId -Window ''
Check 'cancel cannot overwrite completed answer' ((Ask-Result).result-eq 'answered')
Check 'ask input never reaches terminal' ((Rpc 'session.text' @{all=$true} $session)-ceq $text)
Open-Ask $form
[void][HudOwnedJob]::SendMessageW((Ask-Control 200),0x106,[IntPtr][int][char]'r',[IntPtr]1)
Check 'Latin shortcut uses same button contract' ((Ask-Result).button-eq 'revise')
foreach($reason in 'escape','closed','cancel') {
    Open-Ask $form
    if($reason-eq 'escape'){Ask-Key 27}elseif($reason-eq 'closed'){[void][HudOwnedJob]::SendMessageW($ask,0x10,[IntPtr]::Zero,[IntPtr]::Zero)}else{Ask-Click 2}
    Check "distinct cancellation reason $reason" ((Ask-Result).reason-eq $reason)
}
$bad=Rpc 'ask.open' @{form=@{fields=@(@{id='bad';label='Bad';type='html'})}} -NoTarget -AllowError
Check 'invalid raw form creates no UI' (-not $bad.ok -and [AskProbe]::Find($hwnd)-eq [IntPtr]::Zero)
$pick=[string](Rpc 'pick.open' @{items=@();allowCustom=$true} -NoTarget).id
$busy=Rpc 'ask.open' @{form=@{}} -NoTarget -AllowError
Check 'pending picker refuses ask' (-not $busy.ok)
$null=Rpc 'pick.cancel' @{} $pick -Window ''
$large=@{prompt='Many fields';fields=@(0..15|ForEach-Object{@{id="f$_";label="Field $_";type='text'}});buttons=@(0..7|ForEach-Object{@{id="b$_";label="Answer $_"}})}
Open-Ask $large
foreach($dpi in 96,192){
    $oldDpi=[HudOwnedJob]::SetThreadDpiAwarenessContext([IntPtr](-4))
    try{
        if($dpi-eq 96){[AskProbe]::Dpi($ask,$dpi,640,480)}else{[AskProbe]::Dpi($ask,$dpi,960,800)}
        $fits=$true
        foreach($controlId in @(200,2,3,4)+@(1000..1007)){if(-not [AskProbe]::Fits($ask,(Ask-Control $controlId))){$fits=$false}}
        Check "16-field 8-button form fits compact bounds at DPI $dpi" $fits
    }finally{[void][HudOwnedJob]::SetThreadDpiAwarenessContext($oldDpi)}
}
for($page=0;$page-lt 15;$page++){Ask-Click 4}
Check 'all sixteen fields remain reachable through native paging' ([AskProbe]::Text((Ask-Control 101))-match '16/16')
$originalHwnd=$hwnd;try{$hwnd=$ask;$null=Shot 'native-ask-192-compact'}finally{$hwnd=$originalHwnd}
Ask-Click 1007
Check 'last custom button returns all sixteen empty typed answers' ((Ask-Result).button-eq 'b7' -and @((Ask-Result).values.PSObject.Properties).Count-eq 16) ((Ask-Result)|ConvertTo-Json -Compress -Depth 8)
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$opened='{}'|& $ctl ask --no-block --pipe $pipe|ConvertFrom-Json
Check 'actual ask CLI returns generated no-block ID' ($LASTEXITCODE-eq 0 -and $opened.id)
$pending=& $ctl ask result $opened.id --pipe $pipe|ConvertFrom-Json
Check 'actual ask pending exit is one' ($LASTEXITCODE-eq 1 -and $pending.result-eq 'pending')
$null=& $ctl ask cancel $opened.id --pipe $pipe
$cancelled=& $ctl ask result $opened.id --pipe $pipe|ConvertFrom-Json
Check 'actual ask cancellation returns reason and exit two' ($LASTEXITCODE-eq 2 -and $cancelled.reason-eq 'cancel')
$owner=[string]((Rpc 'window.list').windows|Where-Object active|Select-Object -First 1).id
Open-Ask $form;$closing=$askId
$other=Rpc 'window.new' @{name='Ask other owner'} -NoTarget
for($i=0;$i-lt 50 -and @((Rpc 'window.list').windows|Where-Object {$_.id-eq $other -and $_.active}).Count-eq 0;$i++){Start-Sleep -Milliseconds 100}
Check 'exact ask ID survives active window change' ((Ask-Result $closing).result-eq 'pending')
$wrong=Rpc 'ask.result' @{} $closing -Window 'active' -AllowError
Check 'explicit wrong owner refuses ask result' (-not $wrong.ok)
$null=Rpc 'window.close' @{} $owner -Window ''
for($i=0;$i-lt 50 -and (Ask-Result $closing).result-eq 'pending';$i++){Start-Sleep -Milliseconds 100}
Check 'owner close settles and retains explicit owner-closed reason' ((Ask-Result $closing).reason-eq 'owner-closed')
