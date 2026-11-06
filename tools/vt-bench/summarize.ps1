param(
    [Parameter(Mandatory)][string[]]$Reports,
    [Parameter(Mandatory)][string]$OutputCsv
)
$ErrorActionPreference = 'Stop'
function Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -ne 5) { throw 'Expected five samples' }
    return $sorted[2].ToString($null, [Globalization.CultureInfo]::InvariantCulture)
}
$summary = foreach ($path in $Reports) {
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1 -or $report.results.Count -ne 54) {
        throw "Incomplete or unsupported report: $path"
    }
    foreach ($result in $report.results) {
        foreach ($sample in $result.samples) {
            if ($sample.elapsedMs -le 0 -or $sample.inputBytes -le 0) {
                throw "Invalid measurement in $path"
            }
        }
        [pscustomobject][ordered]@{
            backend = $report.backend
            cols = $result.cols
            rows = $result.rows
            sessions = $result.sessions
            scenario = $result.scenario
            chunkSize = $result.chunkSize
            medianElapsedMs = Median $result.samples.elapsedMs
            medianCpuMs = Median $result.samples.cpuMs
            medianAllocatedBytes = Median $result.samples.allocatedBytes
            medianMiBPerSecond = Median @($result.samples | ForEach-Object {
                $_.inputBytes / 1MB / ($_.elapsedMs / 1000)
            })
            medianPrivateBytes = Median $result.samples.PrivateMemorySize64
            medianWorkingSetBytes = Median $result.samples.WorkingSet64
        }
    }
}
$summary | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding utf8
