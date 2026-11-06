param([Parameter(Mandatory)][string[]]$Samples,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$culture=[Globalization.CultureInfo]::InvariantCulture
function Number([string]$value){[double]::Parse($value,$culture)}
function Percentile([double[]]$values,[double]$p){
    [array]::Sort($values)
    return $values[[Math]::Max(0,[Math]::Ceiling($values.Length*$p)-1)]
}
function Window($observations){
    $latencies=[double[]]@($observations | ForEach-Object {Number $_.echoMs})
    $private=[double[]]@($observations | ForEach-Object {((Number $_.hostPrivate)+(Number $_.clientPrivate))/1MB})
    [pscustomobject]@{
        samples=$latencies.Length;p50Ms=Percentile $latencies .5;p95Ms=Percentile $latencies .95;p99Ms=Percentile $latencies .99
        medianPrivateMiB=Percentile $private .5;peakPrivateMiB=($private|Measure-Object -Maximum).Maximum
        maxHandles=($observations | ForEach-Object {[long]$_.hostHandles+[long]$_.clientHandles}|Measure-Object -Maximum).Maximum
    }
}
$runs=@(foreach($file in $Samples){
    $rows=@(Import-Csv -LiteralPath $file)
    if(-not $rows.Length){throw "No samples in $file"}
    $expected=@{}
    foreach($row in $rows){
        $next=1+[int]$expected[$row.slot]
        if([int]$row.sequence -ne $next){throw "Echo sequence gap in slot $($row.slot)"}
        $expected[$row.slot]=$next
    }
    $seconds=Number $rows[-1].seconds
    $span=$seconds/4
    [pscustomobject]@{
        source=[IO.Path]::GetFullPath($file);sessions=$expected.Count;durationSeconds=$seconds
        samples=$rows.Count;overall=Window $rows
        firstQuarter=Window @($rows|Where-Object {(Number $_.seconds) -le $span})
        thirdQuarter=Window @($rows|Where-Object {(Number $_.seconds) -gt 2*$span -and (Number $_.seconds) -le 3*$span})
        lastQuarter=Window @($rows|Where-Object {(Number $_.seconds) -gt 3*$span})
        intervalCpuSeconds=(((Number $rows[-1].hostCpuMs)-(Number $rows[0].hostCpuMs))+
            ((Number $rows[-1].clientCpuMs)-(Number $rows[0].clientCpuMs)))/1000
    }
})
[pscustomobject]@{runs=$runs;endpoint='semantic Enter to decoded viewport; includes polling, no EndDraw';
    resources='host plus headless client, excludes children';durationSeconds=($runs|Measure-Object durationSeconds -Sum).Sum} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output
