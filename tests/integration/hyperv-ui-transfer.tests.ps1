# Host-free adversarial archive/protocol checks; no VM or host UI is launched.
#Requires -Version 7.0
$ErrorActionPreference='Stop'
Add-Type -Path "$PSScriptRoot/hyperv-ui-transfer.cs"
$root=Join-Path ([IO.Path]::GetTempPath()) ('agwinterm-transfer-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$checks=0
function Check([string]$Name,[bool]$Success) { if(-not $Success){throw "FAIL $Name"};$script:checks++;Write-Host "PASS $Name" }
function Reject([string]$Name,[scriptblock]$Action) { $failed=$false;try{& $Action}catch{$failed=$true};Check $Name $failed }
function Make-Zip([string[]]$Names,[int]$Attributes=0) {
    $path=Join-Path $root ([guid]::NewGuid().ToString('N')+'.zip')
    $zip=[IO.Compression.ZipFile]::Open($path,'Create')
    try{foreach($name in $Names){$entry=$zip.CreateEntry($name);$entry.ExternalAttributes=$Attributes;$stream=$entry.Open();try{$stream.WriteByte(65)}finally{$stream.Dispose()}}}finally{$zip.Dispose()}
    return $path
}
try {
    $valid=Make-Zip @('tests/проверка.txt','runtimes/a.dll')
    $destination=Join-Path $root 'valid'
    [HyperVUiTransfer]::Extract($valid,$destination)
    Check 'normal Unicode payload is preserved' ((Get-Content "$destination/tests/проверка.txt" -Raw) -eq 'A')
    foreach($name in @('../outside.txt','a/../../outside.txt','C:/outside.txt','/outside.txt','a:stream','a/CON.txt','a/../x','a/x.','a//x','a/NUL')){
        $zip=Make-Zip @($name)
        Reject "unsafe path $name is rejected" { [HyperVUiTransfer]::Extract($zip,(Join-Path $root ([guid]::NewGuid().ToString('N')))) }
    }
    $duplicate=Make-Zip @('a.txt','A.txt')
    Reject 'case-insensitive duplicates are rejected' { [HyperVUiTransfer]::Extract($duplicate,(Join-Path $root 'duplicate')) }
    $link=Make-Zip @('link') 0x400
    Reject 'Windows reparse entries are rejected' { [HyperVUiTransfer]::Extract($link,(Join-Path $root 'link')) }
    $unixLink=Make-Zip @('link') ([int]::MinValue -bor 0x20000000)
    Reject 'Unix symlink entries are rejected' { [HyperVUiTransfer]::Extract($unixLink,(Join-Path $root 'unix-link')) }
    $outside=Join-Path $root 'outside';$null=New-Item -ItemType Directory -Path $outside
    $tree=Join-Path $root 'tree';$null=New-Item -ItemType Directory -Path $tree
    $null=New-Item -ItemType Junction -Path (Join-Path $tree 'junction') -Target $outside
    Reject 'guest evidence junctions are rejected without traversing them' { [HyperVUiTransfer]::ValidateTree($tree) }
    Reject 'archive extraction rejects an existing destination junction' { [HyperVUiTransfer]::Extract((Make-Zip @('junction/file.txt')),$tree) }
    Check 'no file was written through the junction' (-not (Test-Path "$outside/file.txt"))
    Remove-Item -LiteralPath (Join-Path $tree 'junction')
    $stream=[IO.MemoryStream]::new()
    try{[HyperVUiTransfer]::SendText($stream,'{"test":"Quick"}');$stream.Position=0;Check 'framed messages round-trip' ([HyperVUiTransfer]::ReceiveText($stream,1) -eq '{"test":"Quick"}') }finally{$stream.Dispose()}
    $stream=[IO.MemoryStream]::new([BitConverter]::GetBytes([int]70000))
    try{Reject 'oversized headers fail before allocating a body' {[HyperVUiTransfer]::ReceiveText($stream,1)}}finally{$stream.Dispose()}
    $stream=[IO.MemoryStream]::new([BitConverter]::GetBytes([long]([HyperVUiTransfer]::MaxArchiveBytes+1)))
    try{Reject 'oversized uploads fail before creating a file' {[HyperVUiTransfer]::ReceiveFile($stream,(Join-Path $root 'oversized.zip'))}}finally{$stream.Dispose()}
    $stream=[IO.MemoryStream]::new([byte[]](1,0,0,0))
    try{Reject 'truncated frames fail closed' {[HyperVUiTransfer]::ReceiveText($stream,1)}}finally{$stream.Dispose()}
    Write-Host "$checks transfer checks passed"
} finally {
    $full=[IO.Path]::GetFullPath($root)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $full -Leaf) -notlike 'agwinterm-transfer-*'){throw 'Invalid fixture cleanup path'}
    Remove-Item -LiteralPath $full -Recurse -Force
}
