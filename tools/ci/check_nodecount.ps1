<#
  Sums, for every FEN in tools/ci/nodecount.fens, the "nodes" field of the
  last "info" line before "bestmove" on a "go depth 8" search, and compares
  the total against tools/ci/nodecount_ref.txt. The engine must already be
  built (the workflow does that).

  The engine runs with UseNNUE=false on purpose: the embedded network file
  is gitignored, so a CI checkout builds a network-free binary, and the
  classical evaluator is the one path that produces the same node counts on
  every machine. The reference number is therefore tied to the SEARCH and
  the classical evaluator only; regenerate it by hand when either changes,
  noting in the commit which source produced it. Never automatically.
#>

param(
    [string] $EngineCmd = "dotnet",
    [string] $EngineArgs = "run --project src/NoaChess.UCI -c Release --no-build --",
    [string] $FensFile = "tools/ci/nodecount.fens",
    [string] $RefFile = "tools/ci/nodecount_ref.txt",
    [int] $TimeoutSeconds = 120
)

if (-not (Test-Path $FensFile)) {
    Write-Error "FEN file not found: $FensFile"
    exit 1
}
if (-not (Test-Path $RefFile)) {
    Write-Error "Reference file not found: $RefFile"
    exit 1
}

$refText = Get-Content $RefFile -Raw
if ($refText -match '([0-9]+)') {
    $expected = [int]$matches[1]
} else {
    Write-Error "No numeric reference found in $RefFile"
    exit 1
}

$fens = Get-Content $FensFile | Where-Object { $_.Trim().Length -gt 0 }

Write-Output "Starting engine: $EngineCmd $EngineArgs"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $EngineCmd
$psi.Arguments = $EngineArgs
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
# The child inherits the PROCESS working directory, which is not always the
# PowerShell location; pin it so dotnet run finds the project.
$psi.WorkingDirectory = (Get-Location).Path

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$started = $proc.Start()
if (-not $started) {
    Write-Error "Failed to start engine process"
    exit 1
}

$stdIn = $proc.StandardInput
$stdOut = $proc.StandardOutput
$stdErr = $proc.StandardError

function Send-Line($line) {
    $stdIn.WriteLine($line)
    $stdIn.Flush()
    Write-Verbose ">> $line"
}

function Read-Lines-Until($predicate, $timeoutSec) {
    $deadline = [datetime]::UtcNow.AddSeconds($timeoutSec)
    $collected = @()
    while ([datetime]::UtcNow -lt $deadline) {
        if ($stdOut.Peek() -ge 0) {
            $line = $stdOut.ReadLine()
            if ($null -eq $line) { Start-Sleep -Milliseconds 20; continue }
            $collected += $line
            Write-Host $line
            if (& $predicate $line) { return @{ matched = $true; lines = $collected } }
        } else {
            Start-Sleep -Milliseconds 20
        }
    }
    return @{ matched = $false; lines = $collected }
}

$totalNodes = 0

# UCI handshake and set options. The generous timeout is for cold CI
# machines, where dotnet run pays MSBuild evaluation before the engine says
# anything.
# NOTE the call syntax: PowerShell functions take space-separated arguments.
# The original ({ ... }, N) form passed a two-element ARRAY as the predicate
# and null as the timeout, so the read loop never ran even once.
Send-Line "uci"
$res = Read-Lines-Until { param($l) $l -match '^uciok' } 60
if (-not $res.matched) {
    # Surface stderr: a dotnet run failure otherwise dies silently.
    if ($proc.HasExited) {
        $err = $stdErr.ReadToEnd()
        if ($err) { Write-Host $err }
    }
    Write-Error "Engine did not respond with uciok"
    if (-not $proc.HasExited) { $proc.Kill() }
    exit 1
}

Send-Line "setoption name Hash value 64"
Send-Line "setoption name Threads value 1"
Send-Line "setoption name UseNNUE value false"

Send-Line "isready"
$res = Read-Lines-Until { param($l) $l -match '^readyok' } 30
if (-not $res.matched) {
    Write-Error "Engine did not respond readyok after setting options"
    $proc.Kill()
    exit 1
}

foreach ($fen in $fens) {
    $fenTrim = $fen.Trim()
    if ($fenTrim.Length -eq 0) { continue }

    Write-Output "=== Position: $fenTrim ==="

    Send-Line "ucinewgame"
    Send-Line "isready"
    $res = Read-Lines-Until { param($l) $l -match '^readyok' } 30
    if (-not $res.matched) {
        Write-Error "Engine did not respond readyok after ucinewgame"
        $proc.Kill()
        exit 1
    }

    Send-Line "position fen $fenTrim"
    Send-Line "go depth 8"

    $lastNodes = $null
    $foundBest = $false
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($stdOut.Peek() -ge 0) {
            $line = $stdOut.ReadLine()
            if ($null -eq $line) { Start-Sleep -Milliseconds 10; continue }
            Write-Host $line

            if ($line -match '\binfo\b' -and $line -match '\bnodes\b') {
                if ($line -match 'nodes\s+([0-9]+)') {
                    $lastNodes = [int]$matches[1]
                }
            }

            if ($line -match '^\s*bestmove\b') {
                $foundBest = $true
                break
            }
        } else {
            Start-Sleep -Milliseconds 20
        }
    }

    if (-not $foundBest) {
        Write-Error "Timeout waiting bestmove for position: $fenTrim"
        $proc.Kill()
        exit 1
    }

    if ($null -eq $lastNodes) {
        Write-Error "No 'info ... nodes N' line found before bestmove for position: $fenTrim"
        $proc.Kill()
        exit 1
    }

    Write-Output "Nodes for this position: $lastNodes"
    $totalNodes += $lastNodes
}

Send-Line "quit"
Start-Sleep -Milliseconds 100

Write-Output "Total nodes summed: $totalNodes"
Write-Output "Expected nodes: $expected"

if ($totalNodes -ne $expected) {
    Write-Error "Nodecount mismatch: expected $expected but got $totalNodes"
    exit 1
}

Write-Output "Nodecount matches expected value."
exit 0
