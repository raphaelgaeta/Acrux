# Checks every file of this folder against SHA256SUMS (the 9 GB one takes ~1 min).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File C:\AcruxBench\verify.ps1
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bad = 0; $n = 0
foreach ($line in Get-Content (Join-Path $here 'SHA256SUMS')) {
    $hash, $rel = $line -split '  ', 2
    $path = Join-Path $here ($rel -replace '/', '\')
    $n++
    if (-not (Test-Path -LiteralPath $path)) { Write-Host "MISSING $rel"; $bad++; continue }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLower() -ne $hash) { Write-Host "DIFFERENT $rel"; $bad++ }
}
Write-Host "$n files checked, $bad wrong"
if ($bad -gt 0) { exit 1 }
