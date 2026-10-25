# Extract only the shape validator: never start the integration runner or a GUI.
param([string]$Exe,[switch]$Strict)
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'conformance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Conformance runner has parse errors'}
$function=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-Shape'},$true)
if(-not $function){throw 'Missing production shape validator'}
. ([scriptblock]::Create($function.Extent.Text))
$checked=0;$failed=0
foreach($case in @(
    @{Json='{"ok":true,"result":[]}';Kind='array';Good=$true},
    @{Json='{"ok":true,"result":[{"id":"a"}]}';Kind='array';Good=$true},
    @{Json='{"ok":true,"result":[{"id":"a"},{"id":"b"}]}';Kind='array';Good=$true},
    @{Json='{"ok":true,"result":{"id":"a"}}';Kind='array';Good=$false},
    @{Json='{"ok":true,"result":""}';Kind='string';Good=$true},
    @{Json='{"ok":true,"result":null}';Kind='string';Good=$false},
    @{Json='{"ok":true,"result":0}';Kind='integer';Good=$true},
    @{Json='{"ok":true,"result":"0"}';Kind='integer';Good=$false},
    @{Json='{"ok":true,"result":0.5}';Kind='integer';Good=$false},
    @{Json='{"ok":true,"result":null}';Kind='integer';Good=$false},
    @{Json='{"ok":true,"result":{"id":"a"}}';Kind='object';Fields=@('id');Good=$true},
    @{Json='{"ok":true,"result":{}}';Kind='object';Fields=@('id');Good=$false},
    @{Json='{"ok":false,"error":"refused"}';Kind='object';Good=$false},
    @{Json='{"id":"a"}';Kind='object';Fields=@('id');Payload=$true;Good=$true},
    @{Json='{"result":"cancelled"}';Kind='object';Fields=@('result');Payload=$true;Good=$true},
    @{Json='{"id":"a"}';Kind='object';Fields=@('id');Good=$false}
)){
    ++$checked
    try{$reason=Test-Shape ($case.Json|ConvertFrom-Json) $case.Kind $case.Fields ([bool]$case.Payload)
        $ok=($null -eq $reason) -eq $case.Good
    }catch{$ok=$false;$reason=$_.Exception.Message}
    if(-not $ok){++$failed;"FAIL $($case.Kind): $($case.Json) — $reason"}
}
$refusalFunction=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-Refusal'},$true)
if(-not $refusalFunction){throw 'Missing production refusal validator'}
. ([scriptblock]::Create($refusalFunction.Extent.Text))
foreach($case in @(
    @{Reply=$null;Code=2;Diagnostic='invalid size';Allow=$true;Good=$true},
    @{Reply=$null;Code=2;Diagnostic='invalid size';Allow=$false;Good=$false},
    @{Reply=$null;Code=0;Diagnostic='invalid size';Allow=$true;Good=$false},
    @{Reply=$null;Code=2;Diagnostic='';Allow=$true;Good=$false},
    @{Reply=[pscustomobject]@{__raw='unexpected output'};Code=2;Diagnostic='invalid size';Allow=$true;Good=$false},
    @{Reply=[pscustomobject]@{ok=$true;result='opened'};Code=0;Diagnostic='';Allow=$true;Good=$false},
    @{Reply=[pscustomobject]@{ok=$false;error='refused'};Code=1;Diagnostic='';Allow=$false;Good=$true}
)){
    ++$checked
    if ((Test-Refusal $case.Reply $case.Code $case.Diagnostic $case.Allow) -ne $case.Good) {
        ++$failed; "FAIL refusal validator: exit=$($case.Code), allowUsage=$($case.Allow)"
    }
}
"conformance-validator: $checked checks, $failed failed; no app launched"
exit $(if($failed){1}else{0})
