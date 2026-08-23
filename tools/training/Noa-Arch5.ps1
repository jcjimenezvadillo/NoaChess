# Does the rebuilt head evaluate better? Answered in about twelve hours, not ten
# days.
#
# THE QUESTION. Architecture 5 replaces a head that is one linear layer with one
# clipped ReLU by the shape a modern reference evaluation actually uses: the
# feature transformer read PAIRWISE (a genuine second-order interaction between
# features, at the first layer), each hidden layer emitting its square next to
# its clipped activation, a second hidden layer the output reads past, and a
# linear bypass. The arithmetic is implemented and verified exact against the
# trainer in both directions. What is NOT known is whether it is worth anything,
# and no amount of code reading will say.
#
# WHY TWO ARMS AND NOT A COMPARISON AGAINST THE SHIPPING NET. fq60 has sixty
# epochs and QA=255 behind it. A three-epoch arch 5 would lose to it for reasons
# that have nothing to do with the architecture, and the result would be
# uninterpretable. So both arms are trained here, identical in every argument
# except --dual, and played against each other. The only difference between the
# two nets is the shape of their head.
#
# WHY THREE EPOCHS IS ENOUGH FOR A FIRST READ. The threat features measured
# +60.4 Elo at fixed nodes with three epochs against their own three-epoch
# control, and that read held up. Three epochs is roughly six hours per arm
# against the hundred and twenty a sixty-epoch arm costs, so this asks the
# question at a twentieth of the price. If it comes out flat or negative, the
# long run is not worth starting.
#
# COST NOTE. Neither arm uses threat features, so neither needs the CSR cache -
# only the .features.npz caches, which the corpus already has. Nothing here
# rebuilds anything.
#
# BEFORE RUNNING: the GPU must be FREE. Measured 2026-08-21 at 96% utilisation
# with the threat training on it; starting a second run there does not go twice
# as fast, it makes both take twice as long.

param(
    [string]$Corpus = "C:\NoaData\corpus-fq60",
    [int]$Epochs = 3,

    # Every one of these mirrors the recipe the shipping net was trained with,
    # so the control arm is the familiar net and not a new variable.
    [int]$FtOut = 128,
    [int]$L1Out = 32,
    [int]$L2Out = 32,
    [int]$Batch = 16384,
    [double]$Lr = 1e-3,
    [double]$Lambda = 0.85,

    # QA=127 on BOTH arms. Architecture 5 is an int8 head and int8 requires it
    # for the VPMADDUBSW lane to stay exact; forcing the control to the same
    # value keeps quantisation from becoming a second difference between them.
    [int]$Qa = 127
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$nnue = Join-Path $PSScriptRoot "nnue"
$models = Join-Path $root "models\nnue"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$log = Join-Path $root "logs\arch5-$stamp.log"

function Say($text) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $text
    Write-Host $line
    Add-Content -Path $log -Value $line
}

$shards = @(Get-ChildItem -Path (Join-Path $Corpus "*.noadata") -File |
            ForEach-Object { $_.FullName })
if ($shards.Count -eq 0) { throw "no shards found in $Corpus" }

Say "corpus : $Corpus ($($shards.Count) shards)"
Say "arms   : arch 5 (--dual) and its control, identical except that flag"
Say "shape  : ft=$FtOut l1=$L1Out l2=$L2Out qa=$Qa epochs=$Epochs"
Say ""

Push-Location $nnue
try {
    foreach ($arm in @(
        @{ Name = "a5dual"; Extra = @("--dual", "--l2-out", "$L2Out"); Arch = 5 },
        @{ Name = "a5ctrl"; Extra = @();                               Arch = 2 }
    )) {
        $ckpt = "checkpoints/$($arm.Name).pt"
        Say "=== training $($arm.Name) (arch $($arm.Arch)) ==="

        $trainArgs = @(
            "train_nnue.py",
            "--data") + $shards + @(
            "--out", $ckpt,
            "--epochs", "$Epochs",
            "--batch", "$Batch",
            "--lr", "$Lr",
            "--lambda", "$Lambda",
            "--ft-out", "$FtOut",
            "--l1-out", "$L1Out",
            "--out-buckets", "1",
            "--factorized",
            "--qat",
            "--qa", "$Qa"
        ) + $arm.Extra

        & python @trainArgs 2>&1 | Tee-Object -FilePath $log -Append
        if ($LASTEXITCODE -ne 0) { throw "$($arm.Name): training failed" }

        $out = Join-Path $models "noa-$($arm.Name).noannue"
        Say "=== exporting $($arm.Name) as arch $($arm.Arch) ==="
        # --arch is passed EXPLICITLY on both arms. The exporter's default has
        # already contaminated one measurement in this project by quantising a
        # net to a grid it was not trained on.
        & python export_model.py --checkpoint $ckpt --out $out --arch $arm.Arch 2>&1 |
            Tee-Object -FilePath $log -Append
        if ($LASTEXITCODE -ne 0) { throw "$($arm.Name): export failed" }
        Say ""
    }
}
finally {
    Pop-Location
}

Say "both nets written to $models"
Say "next: F:\Works\_______________CHESSTEST\sprt_arch5_vs_ctrl.bat"
Say "      fixed nodes, so it measures the EVALUATION and nothing else."
