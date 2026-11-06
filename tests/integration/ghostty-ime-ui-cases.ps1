# Runs after real Pinyin provisioning in web-ime-acceptance.ps1, on its owned VM.
if ($env:AGWINTERM_HYPERV_UI -ne '1' -or -not (& "$PSScriptRoot/hyperv-ui-context.ps1")) { throw 'Terminal IME requires an owned Hyper-V guest' }
$sink=Join-Path $output 'terminal-input.ps1'
$inputLog=Join-Path $output 'terminal-input.txt'
@'
param([string]$Log)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ImeSink {
 [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int id);
 [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h,uint mode);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool ReadConsoleW(IntPtr h,[Out] char[] text,uint count,out uint read,IntPtr reserved);
 public static void Run(string log) {
  Console.OutputEncoding=new System.Text.UTF8Encoding(false);
  if(!SetConsoleMode(GetStdHandle(-10),0x200)||!SetConsoleMode(GetStdHandle(-11),7))throw new Exception("Console mode");
  Console.Write("\x1b[?9001lTERMINAL-IME-READY Straße\r\n");
  var chars=new char[4096];var bytes=new byte[16384];var encoder=System.Text.Encoding.UTF8.GetEncoder();
  for(;;){if(!ReadConsoleW(GetStdHandle(-10),chars,(uint)chars.Length,out uint count,IntPtr.Zero))throw new Exception("ReadConsoleW");
   int n=encoder.GetBytes(chars,0,(int)count,bytes,0,false);System.IO.File.AppendAllText(log,Convert.ToHexString(bytes,0,n));}
 }
}
"@
[ImeSink]::Run($Log)
'@ | Set-Content -LiteralPath $sink
function TerminalBytes {
    if (-not (Test-Path $inputLog)) { return '' }
    $stream=[IO.File]::Open($inputLog,'Open','Read','ReadWrite')
    $reader=[IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
$command='"'+(Join-Path $PSHOME 'pwsh.exe')+'" -NoProfile -File "'+$sink+'" -Log "'+$inputLog+'"'
$reply=Call @('session','new','--name','ghostty-ime','--command',$command,'--command-mode','direct')
Require $reply.ok 'terminal IME byte sink starts'
$terminal=[string]$reply.result
for($i=0;$i -lt 100;$i++) {
    if ((Call @('session','text','--target',$terminal)).result -match 'TERMINAL-IME-READY') { break }
    Start-Sleep -Milliseconds 100
}
Require ((Call @('session','text','--target',$terminal)).result -match 'TERMINAL-IME-READY') 'terminal IME sink is ready'
# Full Unicode case expansion is implemented by our native feed/tick extension;
# neither legacy matcher can satisfy this probe after a managed loading fallback.
$search=(Call @('session','search','STRASSE','--target',$terminal)).result
for($i=0;$i -lt 100 -and $search -eq 'searching…';$i++) {
    Start-Sleep -Milliseconds 50
    $search=(Call @('session','search','--target',$terminal)).result
}
Require ($search -eq '1 of 1') 'IME sink uses native Ghostty Unicode search, not managed fallback'
$null=Call @('session','search','--close','--target',$terminal)
[ImeKeys]::Focus($handle)
[ImeKeys]::Chinese($handle)
function TerminalCompose([string]$label) {
    $before=TerminalBytes
    foreach($key in 'NIHAO'.ToCharArray()){[ImeKeys]::Key([byte][char]$key)}
    Start-Sleep -Milliseconds 500
    Require ((TerminalBytes) -ceq $before) "${label}: preedit is not sent to terminal"
    $report["terminal-candidate-windows-$label"]=[ImeKeys]::CandidateWindows()
    $composition=@($report["terminal-candidate-windows-$label"] | Where-Object { $_ -match '^MSCTFIME Composition:' })
    Require ($composition.Count -eq 1) "${label}: real Pinyin composition window is visible"
    $bounds=@(($composition[0] -split ':')[1] -split ',' | ForEach-Object {[int]$_})
    $caret=[ImeKeys]::CaretPosition($handle)
    $metrics=(Call @('session','metrics','--target',$terminal)).result
    Require ([Math]::Abs($bounds[0]-$caret[0]) -le $metrics.cellWidth -and [Math]::Abs($bounds[1]-$caret[1]) -le $metrics.cellHeight) "${label}: Pinyin composition is anchored to terminal caret"
    $capture=[Drawing.Bitmap]::new([ImeKeys]::GetSystemMetrics(0),[ImeKeys]::GetSystemMetrics(1))
    $graphics=[Drawing.Graphics]::FromImage($capture)
    try {$graphics.CopyFromScreen(0,0,0,0,$capture.Size);$capture.Save((Join-Path $output ("terminal-candidates-$label.png")))} finally {$graphics.Dispose();$capture.Dispose()}
    [ImeKeys]::Key(0x31)
    for($poll=0;$poll -lt 50 -and (TerminalBytes) -ceq $before;$poll++){Start-Sleep -Milliseconds 100}
    Start-Sleep -Milliseconds 300
    $delta=(TerminalBytes).Substring($before.Length)
    $allowed=@('你好','你号') | ForEach-Object {[Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($_))}
    $report["terminal-$label"]=@{bytes=$delta;layout=[ImeKeys]::Layout($handle)}
    Require ($delta -cin $allowed) "${label}: Pinyin candidate reaches terminal exactly once"
}
TerminalCompose 'initial'
$before=TerminalBytes
foreach($key in 'ZHONG'.ToCharArray()){[ImeKeys]::Key([byte][char]$key)}
[ImeKeys]::Key(0x1b)
Start-Sleep -Milliseconds 500
Require ((TerminalBytes) -ceq $before) 'terminal cancellation sends neither preedit nor Escape'
[void][ImeKeys]::MoveWindow($handle,60,60,900,600,$true)
Start-Sleep -Milliseconds 300
TerminalCompose 'resize'
$null=Call @('session','select','--target',$owner)
Start-Sleep -Milliseconds 200
$null=Call @('session','select','--target',$terminal)
Start-Sleep -Milliseconds 300
[ImeKeys]::Chinese($handle)
TerminalCompose 'refocus'
$null=Call @('session','close','--target',$terminal)
