<#
Measures Acrux (and DuckDB's command line, to compare) on the 9 GB test file
the way bench/ does: each command in a process of its own, with its time and
its peak private memory (commit), sampled every 20 ms. Round 1 is the first
after a reboot only for the command that reads the file first; later rounds
find in the disk cache what the round before read.

It changes nothing on the machine: it only keeps Windows awake while it runs,
and writes results-laptop.json next to itself. Windows PowerShell 5.1 writes
it with a BOM (read it as utf-8-sig).

Usage (make_folder.sh builds the folder):
  powershell -NoProfile -ExecutionPolicy Bypass -File C:\AcruxBench\run.ps1 -Dry   (a check, ~10 s)
  powershell -NoProfile -ExecutionPolicy Bypass -File C:\AcruxBench\run.ps1        (~1 min)
#>
param([int]$Rounds = 4, [switch]$Dry)
$ErrorActionPreference = 'Stop'
Remove-Item Env:AI_AGENT -ErrorAction SilentlyContinue   # DuckDB's command line acts differently with it set

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$file = Join-Path $here 'data\stress_700cols_5Mrows.parquet'
$sqlFile = $file -replace '\\', '/'
$acrux = Join-Path $here 'cli\Acrux.Cli.exe'
$probe = Join-Path $here 'probe\AcruxProbe.exe'
$duck = Join-Path $here 'duckdb\duckdb.exe'

Add-Type -Namespace AcruxBench -Name Power -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
[void][AcruxBench.Power]::SetThreadExecutionState([uint32]2147483649)   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

$threads = [Environment]::ProcessorCount
$pre = "SET threads = $threads; SET memory_limit = '6GB';"
$tasks = @(
    @{ task = 'screen';   tool = 'acrux-app';  exe = $probe; args = "screen `"$file`"" },
    @{ task = 'head10';   tool = 'acrux';      exe = $acrux; args = "head `"$file`" -n 10" },
    @{ task = 'newest10'; tool = 'acrux';      exe = $acrux; args = "filter `"$file`" c009_ts --last `"7 years`" -n 10" },
    @{ task = 'count';    tool = 'acrux';      exe = $acrux; args = "rows `"$file`"" },
    @{ task = 'head10';   tool = 'duckdb-cli'; exe = $duck;  args = "-csv -c `"$pre SELECT * FROM read_parquet('$sqlFile') LIMIT 10`"" },
    @{ task = 'newest10'; tool = 'duckdb-cli'; exe = $duck;  args = "-csv -c `"$pre SELECT * FROM read_parquet('$sqlFile') ORDER BY c009_ts DESC, row_id LIMIT 10`"" },
    @{ task = 'count';    tool = 'duckdb-cli'; exe = $duck;  args = "-csv -c `"$pre SELECT count(*) FROM read_parquet('$sqlFile')`"" }
)

function Invoke-Measured($t) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $t.exe
    $psi.Arguments = $t.args
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $peak = [long]0
    while (-not $p.HasExited) {
        try { $p.Refresh(); $v = $p.PeakPagedMemorySize64; if ($v -gt $peak) { $peak = $v } } catch { }
        Start-Sleep -Milliseconds 20
    }
    $p.WaitForExit()
    $clock.Stop()
    $lines = @($out.Result -split "`r?`n" | Where-Object { $_ -ne '' })
    $op = $null
    if ($t.tool -eq 'acrux-app' -and $p.ExitCode -eq 0 -and $lines.Count -gt 0) { $op = ($lines[$lines.Count - 1] | ConvertFrom-Json).op_s }
    $e = $err.Result
    if ($e.Length -gt 400) { $e = $e.Substring($e.Length - 400) }
    return [ordered]@{
        task = $t.task; tool = $t.tool
        wall_s = [math]::Round($clock.Elapsed.TotalSeconds, 3); op_s = $op
        peak_mb = [math]::Round($peak / 1MB); exit = $p.ExitCode
        first = @($lines | Select-Object -First 4); last = @($lines | Select-Object -Last 2); err = $e
    }
}

$os = Get-CimInstance Win32_OperatingSystem
$info = [ordered]@{
    when = (Get-Date).ToString('s')
    cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
    threads = $threads
    ram_gb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
    windows = "$($os.Caption) $($os.Version)"
    minutes_since_boot = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalMinutes)
    acrux = ((& $acrux version) -join ' ')
    duckdb = ((& $duck --version) -join ' ')
    file_bytes = (Get-Item $file).Length
    defender_realtime = $null; power_plan = $null; on_ac = $null
}
try { $info.defender_realtime = (Get-MpComputerStatus).RealTimeProtectionEnabled } catch { }
try { $info.power_plan = ((powercfg /getactivescheme) -join ' ') } catch { }
try { $info.on_ac = [bool](Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus -ErrorAction Stop | Select-Object -First 1).PowerOnline } catch { }
$info.GetEnumerator() | ForEach-Object { Write-Host ("{0}: {1}" -f $_.Key, $_.Value) }

$list = $tasks
if ($Dry) { $list = @($tasks | Where-Object { $_.task -eq 'count' }); $Rounds = 1 }
$runs = @()
for ($r = 1; $r -le $Rounds; $r++) {
    $free = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 2)
    foreach ($t in $list) {
        $m = Invoke-Measured $t
        $m.round = $r
        $m.free_gb_before_round = $free
        $runs += $m
        Write-Host ("round {0}  {1,-9} {2,-10} {3,7} s  {4,6} MB  exit {5}" -f $r, $m.task, $m.tool, $m.wall_s, $m.peak_mb, $m.exit)
    }
}
$name = 'results-laptop.json'
if ($Dry) { $name = 'results-dry.json' }
$path = Join-Path $here $name
[ordered]@{ info = $info; runs = $runs } | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $path
Write-Host "written to $path"
