# Runs a queue of NNUE candidates unattended: train, export, verify, SPRT,
# report. Launch it once and read the summary later.
#
# WHAT THIS AUTOMATES AND WHAT IT DOES NOT. It removes the manual steps between
# a candidate and its number, which is where every uncontrolled variable so far
# came from: an export arch left at its default, a globbed file list that quietly
# dropped a shard, a hyperparameter typed from memory. Each candidate here
# inherits everything from the baseline checkpoint and changes only the switches
# named in the queue.
#
# It does NOT invent the next candidate. A queue can measure ideas; it cannot
# have them. When this list is exhausted the next one has to be reasoned out.
#
# It never deploys to the bot. Promotion only rewrites NNUE_BEST.txt, and only
# with -Promote, and only for a candidate whose SPRT accepted H1.

param(
    # Each entry: Name, Switches (passed to Noa-DataScale-Train.ps1), Why.
    # The default queue is the two open one-axis questions, in the order that
    # keeps each one interpretable on its own against the SAME baseline.
    # THE BASELINE MOVED. fact60 beat ds1e60 by a wide margin, so measuring the
    # remaining ideas against ds1e60 would answer a question nobody has any more:
    # every one of them would win on the strength of the factorization they all
    # carry, and none of the numbers would say what its own change is worth.
    #
    # So each entry below is factorized, and differs from the fact60 BASELINE in
    # exactly one thing. Read every result as "on top of factorization".
    # THE BASELINE MOVED AGAIN, and this is the second time. fact60 beat ds1e60
    # by +195.4, then fq60 (factorization + quantization-aware training) beat
    # fact60 by +23.5. Every candidate below therefore carries BOTH, and differs
    # from the fq60 baseline in exactly one further thing. Read each result as
    # "on top of the best net there is".
    #
    # fq60 itself is DONE: trained, exported, SPRT'd at +23.5 +/- 15.5 and
    # shipped in the 4.6.2 builds. It is not in this list, and putting it back
    # would just retrain it - which is exactly what happened on 2026-08-10 when
    # a watcher restarted this queue from the top and burned 2.5 hours.
    [object[]]$Queue = @(
        @{ Name = "fqwd0";  Switches = @("-Factorized", "-Qat", "-FtWeightDecay", "0"); Why = "no weight decay on the transformer, on top of fq60" }
        @{ Name = "fqe120"; Switches = @("-Factorized", "-Qat", "-Epochs", "120"); Why = "double the training budget, on top of fq60" }
        @{ Name = "fqloss"; Switches = @("-Factorized", "-Qat", "-LossStyle", "reference"); Why = "reference loss, on top of fq60" }
        @{ Name = "fqlam";  Switches = @("-Factorized", "-Qat", "-StartLambda", "1.0", "-EndLambda", "0.7"); Why = "lambda schedule, on top of fq60" }
    ),

    # The file list still comes from ds1e60, because fact60 and fq60 both
    # inherited it, so all three checkpoints saw the same 70 files. Only the
    # SPRT opponent changed.
    [string]$Baseline = "checkpoints\ds1e60.pt",
    [string]$BaselineNet = "noa-fq60",
    [switch]$Promote,
    [switch]$SkipSprt
)

$ErrorActionPreference = "Stop"
$repo = "F:\Works\Programacion\__Repos\NoaChess"
$test = "F:\Works\_______________CHESSTEST"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$log = Join-Path $repo "logs\nnue-queue-$stamp.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null

function Write-Log($text) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $text
    Write-Host $line
    Add-Content -Path $log -Value $line
}

Write-Log "queue of $($Queue.Count) candidate(s), baseline $Baseline"
Write-Log "log: $log"
$results = @()

foreach ($candidate in $Queue) {
    $name = $candidate.Name
    $switches = @($candidate.Switches)
    $net = Join-Path $repo "models\nnue\noa-$name.noannue"
    Write-Log "=== $name : $($candidate.Why) ==="
    Write-Log "    switches: $($switches -join ' ')"

    $started = Get-Date
    # Tee, and do NOT swallow the passthrough: a nine-hour step that prints
    # nothing is indistinguishable from a hung one, and the epoch lines are the
    # only way to see the run is alive and on schedule.
    & powershell -NoProfile -File (Join-Path $repo "tools/training/Noa-DataScale-Train.ps1") `
        -Baseline $Baseline -Name $name @switches 2>&1 |
        Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) {
        Write-Log "    TRAINING FAILED (exit $LASTEXITCODE); skipping the rest of $name"
        $results += [pscustomobject]@{ Name = $name; Outcome = "training failed"; Elo = "" }
        continue
    }
    $hours = [math]::Round(((Get-Date) - $started).TotalHours, 2)
    Write-Log "    trained in $hours h -> $net"

    if (-not (Test-Path $net)) {
        Write-Log "    EXPORT MISSING; skipping the SPRT"
        $results += [pscustomobject]@{ Name = $name; Outcome = "export missing"; Elo = "" }
        continue
    }

    if ($SkipSprt) {
        $results += [pscustomobject]@{ Name = $name; Outcome = "trained, SPRT skipped"; Elo = "" }
        continue
    }

    # The bat is generated rather than assumed to exist, so a new candidate name
    # never silently measures the previous candidate's net.
    $bat = Join-Path $test "sprt_$name`_vs_$($BaselineNet -replace '^noa-','').bat"
    $template = Get-Content (Join-Path $test "sprt_fact60_vs_ds1e60.bat") -Raw
    $template = $template -replace 'noa-fact60\.noannue', "noa-$name.noannue"
    $template = $template -replace 'noa-ds1e60\.noannue', "$BaselineNet.noannue"
    $template = $template -replace 'name=fact60', "name=$name"
    # The opponent's label too: without this the PGN records the baseline as
    # ds1e60 when it is fq60, and a mislabelled PGN is worse than no PGN.
    $template = $template -replace 'name=ds1e60', "name=$($BaselineNet -replace '^noa-','')"
    $template = $template -replace 'sprt_fact60_vs_ds1e60\.pgn', "sprt_$name.pgn"
    Set-Content -Path $bat -Value $template -Encoding ASCII
    Write-Log "    SPRT: $bat"

    $output = & cmd /c "`"$bat`"" 2>&1 | Tee-Object -FilePath $log -Append
    $verdict = "inconclusive"
    if ($output -match "H1 was accepted") { $verdict = "H1 accepted (better)" }
    elseif ($output -match "H0 was accepted") { $verdict = "H0 accepted (not better)" }
    $elo = ($output | Select-String -Pattern "Elo difference: .*" | Select-Object -Last 1)
    Write-Log "    $verdict   $elo"
    $results += [pscustomobject]@{ Name = $name; Outcome = $verdict; Elo = "$elo" }

    if ($Promote -and $verdict -like "H1*") {
        Set-Content -Path (Join-Path $repo "models\nnue\NNUE_BEST.txt") -Value "noa-$name"
        Write-Log "    PROMOTED to NNUE_BEST.txt (not deployed anywhere; that stays manual)"
    }
}

Write-Log ""
Write-Log "==================== SUMMARY ===================="
foreach ($r in $results) { Write-Log ("{0,-10} {1,-26} {2}" -f $r.Name, $r.Outcome, $r.Elo) }
Write-Log "full log: $log"
