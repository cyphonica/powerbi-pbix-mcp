<#
  Live hand-off proof for the engine-backed offline .pbix operations (eval_dax_offline,
  read_table_offline, get_model_offline, edit_measure_offline).

  This is a MANUAL end-to-end check - it is NOT part of the automated test suite. The suite runs
  headless on a box with no interactive Power BI Desktop, so the open-Desktop path cannot be
  exercised there. Run this on a real Windows machine that HAS Power BI Desktop installed.

  What it does (each call briefly OPENS the .pbix in Power BI Desktop - a Desktop window appears,
  the model loads, the tool runs, then Desktop closes; budget a minute or so per call):
    1. eval_dax_offline  with  EVALUATE ROW("n", 1)   -> expects the single value 1
    2. get_model_offline                              -> reads the schema, picks the first table
    3. read_table_offline on that table               -> expects one or more rows back
  edit_measure_offline is shown at the bottom, commented out, because it WRITES to the file.

  Usage (from the repo root, after `dotnet build src/SuperBiMcp.csproj -c Release`):
    powershell -ExecutionPolicy Bypass -File examples\offline-pbix-handoff.ps1
    powershell -ExecutionPolicy Bypass -File examples\offline-pbix-handoff.ps1 -Pbix "C:\path\to\a-closed-report.pbix"
#>
param(
  [string]$Dll  = "$PSScriptRoot\..\src\bin\Release\net8.0\SuperBiMcp.dll",
  [string]$Pbix = "C:\path\to\a-closed-report.pbix"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Dll))  { throw "SuperBiMcp.dll not found at '$Dll' - build src first (dotnet build src\SuperBiMcp.csproj -c Release)." }
if (-not (Test-Path $Pbix)) { throw "Sample .pbix not found at '$Pbix' - pass -Pbix <path-to-a-closed-.pbix>." }

# --- launch the MCP server and speak JSON-RPC over stdio (stderr is left on the console so its
#     [HH:mm:ss] progress log is visible and can never fill a redirected pipe) ---
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName               = 'dotnet'
$psi.Arguments              = "`"$Dll`""
$psi.UseShellExecute        = $false
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $false
$proc = [System.Diagnostics.Process]::Start($psi)

$script:id = 0
function Rpc([string]$method, $params) {
  $script:id++
  $msg = @{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 20 -Compress
  $proc.StandardInput.WriteLine($msg)
  $proc.StandardInput.Flush()
  while ($true) {
    $line = $proc.StandardOutput.ReadLine()
    if ($null -eq $line) { throw "server closed stdout before answering '$method'." }
    if ($line.Trim().Length -eq 0) { continue }
    try { $obj = $line | ConvertFrom-Json } catch { continue }   # stdout is pure JSON-RPC; skip anything else
    if ($obj.id -eq $script:id) { return $obj }
  }
}
function Notify([string]$method, $params) {
  $msg = @{ jsonrpc = '2.0'; method = $method; params = $params } | ConvertTo-Json -Depth 20 -Compress
  $proc.StandardInput.WriteLine($msg); $proc.StandardInput.Flush()
}
function Tool([string]$name, $arguments) {
  $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
  if ($r.error) { throw "tool '$name' RPC error: $($r.error | ConvertTo-Json -Compress)" }
  return $r.result.content[0].text | ConvertFrom-Json   # the server returns its result as JSON text
}

$fail = 0
try {
  Rpc 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'offline-handoff'; version = '1.0' } } | Out-Null
  Notify 'notifications/initialized' @{}

  Write-Host "`n[1/3] eval_dax_offline  EVALUATE ROW(`"n`", 1)  on  $Pbix"
  $e = Tool 'eval_dax_offline' @{ pbixPath = $Pbix; dax = 'EVALUATE ROW("n", 1)' }
  $n = $e.rows[0][0]
  if ($e.ok -and [double]$n -eq 1) { Write-Host "      PASS - returned n = $n" -ForegroundColor Green }
  else { Write-Host "      FAIL - expected 1, got $($e | ConvertTo-Json -Depth 6 -Compress)" -ForegroundColor Red; $fail++ }

  Write-Host "`n[2/3] get_model_offline  (read the schema, pick the first table)"
  $m = Tool 'get_model_offline' @{ pbixPath = $Pbix }
  $firstTable = $null
  if ($m.ok -and $m.model.tables.Count -gt 0) {
    $firstTable = $m.model.tables[0].name
    Write-Host "      PASS - $($m.model.tableCount) table(s); first is '$firstTable'" -ForegroundColor Green
  } else { Write-Host "      FAIL - no tables in $($m | ConvertTo-Json -Depth 6 -Compress)" -ForegroundColor Red; $fail++ }

  if ($firstTable) {
    Write-Host "`n[3/3] read_table_offline  TOPN(5, '$firstTable')"
    $t = Tool 'read_table_offline' @{ pbixPath = $Pbix; table = $firstTable; topN = 5 }
    if ($t.ok -and $t.rowCount -ge 1) { Write-Host "      PASS - $($t.rowCount) row(s), columns: $($t.columns -join ', ')" -ForegroundColor Green }
    else { Write-Host "      FAIL - expected rows, got $($t | ConvertTo-Json -Depth 6 -Compress)" -ForegroundColor Red; $fail++ }
  }

  # ---------------------------------------------------------------------------------------------
  # edit_measure_offline WRITES to the .pbix (it drives Desktop's Ctrl+S), so it is left commented
  # out to avoid mutating the sample. Uncomment to try it against a throwaway copy:
  #
  #   $ed = Tool 'edit_measure_offline' @{ pbixPath = $Pbix; table = $firstTable;
  #           name = 'Handoff Probe'; expression = '1'; formatString = '0' }
  #   Write-Host ($ed | ConvertTo-Json -Depth 6)
  # ---------------------------------------------------------------------------------------------
}
finally {
  try { $proc.StandardInput.Close() } catch { }
  try { if (-not $proc.WaitForExit(5000)) { $proc.Kill() } } catch { }
}

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL CHECKS PASSED" -ForegroundColor Green; exit 0 }
else { Write-Host "$fail CHECK(S) FAILED" -ForegroundColor Red; exit 1 }
