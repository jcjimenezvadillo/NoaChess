<#
  Comprueba el recuento de nodos sumando, para cada FEN en tools/ci/nodecount.fens,
  el campo 'nodes' de la última línea "info" previa al "bestmove" cuando se hace
  "go depth 8".

  Requisitos: el motor debe estar ya construido (el workflow hará dotnet build antes).
  El script arranca el UCI engine con "dotnet run --project src/NoaChess.UCI -c Release --no-build --"
  y habla UCI directamente.

  Exit code:
    0 -> total de nodes coincide con nodecount_ref.txt
    1 -> no coincide o error
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

# Read expected value (first non-empty numeric token)
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

# helper to send line and flush
function Send-Line($line) {
    $stdIn.WriteLine($line)
    $stdIn.Flush()
    Write-Verbose ">> $line"
}

# helper to read lines with timeout
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

# Initial UCI handshake and options
Send-Line "uci"
# Wait for "uciok"
$res = Read-Lines-Until ({ param($l) $l -match '^uciok' }, 10)
if (-not $res.matched) {
    Write-Error "Engine did not respond with uciok"
    $proc.Kill()
    exit 1
}

# Set required options
Send-Line "setoption name Hash value 64"
Send-Line "setoption name Threads value 1"
Send-Line "setoption name UseNNUE value true"

# Wait isready after options
Send-Line "isready"
$res = Read-Lines-Until ({ param($l) $l -match '^readyok' }, 30)
if (-not $res.matched) {
    Write-Error "Engine did not respond readyok after setting options"
    $proc.Kill()
    exit 1
}

foreach ($fen in $fens) {
    $fenTrim = $fen.Trim()
    if ($fenTrim.Length -eq 0) { continue }

    Write-Output "=== Position: $fenTrim ==="

    # Start fresh for this position: ucinewgame + isready
    Send-Line "ucinewgame"
    Send-Line "isready"
    $res = Read-Lines-Until ({ param($l) $l -match '^readyok' }, 30)
    if (-not $res.matched) {
        Write-Error "Engine did not respond readyok after ucinewgame"
        $proc.Kill()
        exit 1
    }

    Send-Line "position fen $fenTrim"
    Send-Line "go depth 8"

    # Read until 'bestmove' — keep last 'info' line that contains 'nodes'
    $lastNodes = $null
    $foundBest = $false
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($stdOut.Peek() -ge 0) {
            $line = $stdOut.ReadLine()
            if ($null -eq $line) { Start-Sleep -Milliseconds 10; continue }
            Write-Host $line

            if ($line -match '\binfo\b' -and $line -match '\bnodes\b') {
                # Try to capture the nodes field (last occurrence of 'nodes N')
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

# Quit engine cleanly
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
