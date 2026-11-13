$ErrorActionPreference = 'Stop'
$resultPath = Join-Path $PSScriptRoot 'test-results.txt'
if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath }
$process = Start-Process -FilePath (Join-Path $PSScriptRoot 'TrayTodo.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id
    throw 'Self-test timed out.'
}
if ($process.ExitCode -ne 0) { throw "Self-test failed: exit $($process.ExitCode)" }
if (-not (Test-Path $resultPath)) { throw 'Self-test report missing.' }
$result = Get-Content -Raw $resultPath
if (-not $result.StartsWith('PASS:')) { throw $result }
Write-Output $result
