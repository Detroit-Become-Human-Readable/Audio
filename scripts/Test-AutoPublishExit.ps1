$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Test-AutoPublish.ps1'
$escapedPath = $scriptPath.Replace("'", "''")
# Match the Actions pwsh runner: dot-source the script, then propagate LASTEXITCODE.
$runner = ". '$escapedPath'; if (Test-Path variable:\LASTEXITCODE) { exit `$LASTEXITCODE }"
$shell = (Get-Process -Id $PID).Path
$successOutput = & $shell -NoProfile -NonInteractive -Command $runner 2>&1
if ($LASTEXITCODE -ne 0) { throw "Passing automatic publishing checks left a failed runner status:`n$($successOutput -join [Environment]::NewLine)" }
if (($successOutput -join "`n") -notmatch 'Automatic publishing checks passed:') { throw 'The publishing checks did not complete.' }

# Force the first fixture build to fail and verify the script's assertion still escapes.
$failureRunner = 'function global:dotnet { $global:LASTEXITCODE = 1 }; ' + $runner
$failureOutput = & $shell -NoProfile -NonInteractive -Command $failureRunner 2>&1
if ($LASTEXITCODE -eq 0 -or ($failureOutput -join "`n") -notmatch 'Fixture Debug build failed') {
    throw 'A real fixture assertion did not fail the Actions runner.'
}
Write-Output 'Automatic publishing runner checks passed: successful checks exit zero; fixture assertions remain failures.'
$global:LASTEXITCODE = 0
