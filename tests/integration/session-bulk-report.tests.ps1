$ErrorActionPreference='Stop'
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('agwinterm-bulk-report-'+[guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory $temporary)
$files=[Collections.Generic.List[string]]::new()
$folders=[Collections.Generic.List[string]]::new()
try {
    for($pair=0;$pair -lt 5;$pair++) {
        foreach($variant in 'legacy','single-owner') {
            $folder=Join-Path $temporary "$pair-$variant"; [void](New-Item -ItemType Directory $folder); $folders.Add($folder)
            $path=Join-Path $folder 'session-bulk.json'; $files.Add($path)
            $candidate=$variant -eq 'single-owner'
            @{schema=1;hostMode='server';presentation=$variant;traceMode='off';bytes=1MB;elapsedSeconds=10;
              cpuLowerMs=$(if($candidate){79}else{100});cpuUpperMs=$(if($candidate){80}else{101});
              bytesPerSecond=$(if($candidate){120}else{100});meanPrivateBytes=$(if($candidate){12}else{10});
              grid=@{cols=80;rows=24;cellWidth=9;cellHeight=19};binaryHashes=@{fixture='synthetic-only'};
              utc=[DateTime]::UnixEpoch.AddSeconds($pair*2+[int]$candidate).ToString('O')} |
                ConvertTo-Json -Depth 4|Set-Content -LiteralPath $path
        }
    }
    $output=Join-Path $temporary 'report.json'; $files.Add($output)
    $null=& "$PSScriptRoot/../../tools/session-bulk-report.ps1" -Directory $temporary -Output $output
    $report=Get-Content -LiteralPath $output -Raw|ConvertFrom-Json
    $cpu=$report.comparisons|Where-Object metric -eq 'cpuUpperRatio'
    $throughput=$report.comparisons|Where-Object metric -eq 'throughputRatio'
    $memory=$report.comparisons|Where-Object metric -eq 'privateBytesRatio'
    if($cpu.outcome -ne 'pass' -or [Math]::Abs($cpu.ratio.mean-.8) -gt 1e-8 -or
        $throughput.outcome -ne 'pass' -or $memory.outcome -ne 'informational'){throw 'Incorrect fixed-work gate calculation'}
    foreach($path in $files | Where-Object {$_ -like '*single-owner*'}){
        $data=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $data.cpuLowerMs=90; $data.cpuUpperMs=120
        $data | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
    }
    $null=& "$PSScriptRoot/../../tools/session-bulk-report.ps1" -Directory $temporary -Output $output
    $report=Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if(@($report.comparisons | Where-Object {$_.metric -like 'cpu*' -and $_.outcome -ne 'inconclusive'}).Count){throw 'A wide CPU sampling interval proved a false result'}
    foreach($path in $files | Where-Object {$_ -like '*single-owner*'}){
        $data=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $data.cpuLowerMs=119; $data.cpuUpperMs=120
        $data | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
    }
    $null=& "$PSScriptRoot/../../tools/session-bulk-report.ps1" -Directory $temporary -Output $output
    $report=Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if(($report.comparisons | Where-Object metric -eq 'cpuLowerRatio').outcome -ne 'fail'){throw 'A proven CPU regression was missed'}
    Remove-Item -LiteralPath $files[0]
    $rejected=$false
    try{$null=& "$PSScriptRoot/../../tools/session-bulk-report.ps1" -Directory $temporary -Output $output}catch{$rejected=$true}
    if(-not $rejected){throw 'Accepted an unpaired comparison'}
    'Bulk report confidence/gate and missing-pair checks passed'
} finally {
    foreach($file in $files){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file}}
    foreach($folder in $folders){Remove-Item -LiteralPath $folder}
    Remove-Item -LiteralPath $temporary
}
