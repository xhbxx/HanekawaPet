param([string]$ReleaseDirectory = 'dist-ai', [string]$OutputDirectory = 'artifacts/checks')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = [IO.Path]::GetFullPath((Join-Path $root $ReleaseDirectory))
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$exe = Join-Path $release 'HanekawaPet.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the portable app first.' }
New-Item -ItemType Directory -Path "$output/framework/fixtures" -Force | Out-Null
Copy-Item -Path "$root/qa/framework/fixtures/*" -Destination "$output/framework/fixtures" -Force
foreach ($test in @(@('--self-test','framework'), @('--routine-smoke-test','routine'), @('--gacha-smoke-test','gacha'))) {
    $arguments = @('--data-dir', ('"' + "$output/$($test[1])-data" + '"'), $test[0], ('"' + "$output/$($test[1])" + '"'))
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(240000)) { Stop-Process -Id $process.Id; throw "$($test[1]) timed out." }
    if ($process.ExitCode -ne 0) { throw "$($test[1]) failed. See $output" }
}
$framework = Get-Content "$output/framework/framework-checks.json" -Raw | ConvertFrom-Json
$routine = @(Get-Content "$output/routine/results.json" -Raw | ConvertFrom-Json)
$gacha = @(Get-Content "$output/gacha/results.json" -Raw | ConvertFrom-Json)
if (-not $framework.ok -or @($routine + $gacha | Where-Object { -not $_.passed }).Count) { throw 'Checks failed.' }
[ordered]@{ framework = $framework.checks.Count; routine = $routine.Count; gacha = $gacha.Count; passed = $true } |
    ConvertTo-Json | Tee-Object -FilePath "$output/summary.json"
