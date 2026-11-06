param()
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test PtyProcessContracts -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$filter=@('ConPtyConnectionTests','FreshEnvironmentTests','PtyHostTests','ServerSessionTests',
    'SettleOutputTests','SurfaceCursorTests','TerminalSessionStartFailureTests') |
    ForEach-Object {"FullyQualifiedName~$_"}
$evidence=Join-Path $root '.revmux/pty-process'
$null=New-Item -ItemType Directory $evidence -Force
& (Join-Path $env:DOTNET_ROOT 'dotnet.exe') (Join-Path $root 'vstest/vstest.console.dll') (Join-Path $root 'pty-tests/Agwinterm.Pty.Tests.dll') "/TestCaseFilter:$($filter -join '|')" "/ResultsDirectory:$evidence" '/Logger:trx;LogFileName=process.trx'
if($LASTEXITCODE -ne 0){throw "PTY process contracts failed: $LASTEXITCODE"}
if(-not(Test-Path (Join-Path $evidence 'process.trx'))){throw 'Missing process test evidence'}
