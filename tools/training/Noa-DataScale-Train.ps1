# Trains an NNUE candidate against the ds1e60 baseline, differing in ONE axis.
#
# WHAT THIS SCRIPT GOT WRONG BEFORE, AND WHY IT IS BUILT THIS WAY NOW.
#
# The ds2 run was meant to test "more data at equal compute" and measured
# -108.6 Elo. It turned out to differ from its baseline in THREE ways at once,
# none of them the intended one:
#
#   1. EPOCHS 60 -> 22. The intended axis was data volume, and the epoch count
#      was lowered to hold training compute constant. But --max-records has no
#      effect on the streaming loader: FeatureStore takes only (paths,
#      val_fraction), so the baseline had ALREADY read the whole 324M corpus.
#      Both runs saw the same data and the candidate simply got 2.7x less
#      training. The data axis was never open; it was already exhausted.
#   2. EXPORT ARCH 2 vs 1. This script called export_model.py without --arch,
#      whose default is 2 (int8 L1, QA=127), while the baseline net was arch 1
#      (QA=255). That doubles the accumulator quantization error, measured at
#      0.00382 vs 0.00192 in float units, and kills 4.2 points more of the
#      feature transformer. An uncontrolled variable introduced by the script.
#   3. A DIFFERENT FILE LIST. The baseline included selfplay-gen8; the candidate
#      silently did not, because the list was globbed rather than inherited.
#
# So: the file list is now READ OFF THE BASELINE CHECKPOINT, the export arch is
# read off the baseline net, and every hyperparameter is copied from the
# baseline's own recorded args. The only thing a caller can change is the axis
# under test.

param(
    # The run every candidate is compared against. Its dataset list and its
    # hyperparameters define what "unchanged" means.
    [string]$Baseline = "checkpoints\ds1e60.pt",

    # Feature factorization: 704 virtual (piece, square) features during
    # training, folded exactly into the 22,528 real rows at export. The engine
    # and the file format are untouched. This is the axis under test.
    [switch]$Factorized,

    # Quantization-aware training. The engine's evaluation currently differs
    # from the trained net by 16.6% on average (38.8 cp of a 231 cp mean), and
    # 38.77 of those 38.8 come from the feature transformer. With this on, the
    # two agree to under 1 cp - verified by verify_qat.py.
    #
    # ONE AXIS AT A TIME: run this against the ds1e60 baseline WITHOUT
    # -Factorized, so its number means "what quantization-aware training is
    # worth" and not "what two changes are worth together".
    [switch]$Qat,

    # Weight decay for the feature transformer alone. NaN means "inherit the
    # head's 1e-5", which is what every net so far used. Pass 0 to match the
    # reference trainer, which decays only the dense layers.
    [double]$FtWeightDecay = [double]::NaN,

    # "reference" swaps the plain sigmoid target for an antisymmetric win-rate
    # mapping with its own offset and scaling per side, and raises the exponent
    # from 2 to 2.5. The constants are THEIRS, fitted to THEIR evaluation
    # distribution; if this axis shows anything, the constants are worth a sweep
    # of their own before believing the exact numbers.
    [ValidateSet("mse", "reference")]
    [string]$LossStyle = "mse",

    # Lambda schedule. NaN on both means "hold 0.85 constant", which is what
    # every net so far used.
    [double]$StartLambda = [double]::NaN,
    [double]$EndLambda = [double]::NaN,
    [int]$PsqtBuckets = 0,
    [double]$InOffset = [double]::NaN,
    [double]$InScaling = [double]::NaN,
    [double]$OutOffset = [double]::NaN,
    [double]$OutScaling = [double]::NaN,

    # 60 is the baseline's value. Change it only when EPOCHS is the axis.
    [int]$Epochs = 60,

    # 128 is the baseline's width, and the reference runs 1024. Width was
    # measured at 512 on 2026-08-08 and REJECTED at -76/-93, which closed the
    # capacity axis in the CHANGELOG - but that was two days before feature
    # factorization, when 85.6% of the transformer quantised to zero. Widening
    # makes exactly that defect worse: the same signal spread over more neurons
    # gives smaller per-weight magnitudes, and small weights are what rounds
    # away. So the rejection was measured through a broken instrument and the
    # axis is open again. Cost is priced and sub-linear (`nnuewidth`: 256 =
    # 1.51x, 512 = 2.64x), so it is a real trade, not free.
    [int]$FtOut = 128,

    # 1 is the baseline's value; the reference runs 8. Note that a bucketed net
    # can only be exported as arch 3, which is int8 with QA=127, so raising this
    # against an arch-1 baseline moves TWO variables at once - buckets AND
    # quantisation. Pair it with a 1-bucket arch-2 control (also int8) if the
    # buckets themselves are the question.
    [int]$OutBuckets = 1,

    # 1 = int16 L1, QA=255, which is what the baseline net and every shipping
    # net use. Do not change this without meaning to.
    [int]$Arch = 1,

    [string]$Name = "fact60"
)

$ErrorActionPreference = "Stop"
$repo  = "F:\Works\Programacion\__Repos\NoaChess"
$train = Join-Path $repo "tools\training\nnue"

Push-Location $train
try {
    # The file list comes from the baseline, and list_checkpoint_data.py exits
    # non-zero if any of those files has moved, so a corpus that no longer
    # matches stops the run instead of quietly training on a different one.
    $shards = @(python list_checkpoint_data.py $Baseline)
    if ($LASTEXITCODE -ne 0) { throw "baseline dataset unavailable; see the missing files above" }
    if ($shards.Count -eq 0) { throw "no dataset recorded in $Baseline" }

    Write-Host "baseline       : $Baseline"
    Write-Host "shards         : $($shards.Count) (inherited from the baseline, not globbed)"
    Write-Host "epochs         : $Epochs"
    Write-Host "factorized     : $Factorized"
    Write-Host "qat            : $Qat"
    Write-Host "ft weight decay: $(if ([double]::IsNaN($FtWeightDecay)) {'inherited (1e-5)'} else {$FtWeightDecay})"
    Write-Host "ft width       : $FtOut$(if ($FtOut -ne 128) {' (AXIS UNDER TEST)'})"
    Write-Host "export arch    : $Arch"
    Write-Host "checkpoint     : checkpoints\$Name.pt"
    Write-Host ""

    # Every hyperparameter below is COPIED FROM the baseline's checkpoint, not
    # remembered: batch 16384, lambda 0.85, lr 1e-3, weight decay 1e-5, val
    # fraction 0.05, seed 1, ft 128, l1 32, one bucket. Verify with:
    #   python dump_args.py checkpoints\ds1e60.pt
    # An ARRAY, not a $null: PowerShell passes $null to a native command as an
    # empty string argument, which argparse rejects outright. An empty array
    # expands to nothing at all.
    $extra = @()
    if ($Factorized) { $extra += "--factorized" }
    # --qa follows -Arch so the net is trained for the arithmetic it ships with.
    if ($Qat) { $extra += "--qat"; $extra += "--qa"; $extra += "$(if ($Arch -eq 1) {255} else {127})" }
    if (-not [double]::IsNaN($FtWeightDecay)) {
        $extra += "--ft-weight-decay"
        $extra += "$FtWeightDecay"
    }
    if ($LossStyle -ne "mse") { $extra += "--loss-style"; $extra += $LossStyle }
    if (-not [double]::IsNaN($StartLambda)) { $extra += "--start-lambda"; $extra += "$StartLambda" }
    if (-not [double]::IsNaN($EndLambda)) { $extra += "--end-lambda"; $extra += "$EndLambda" }
    if ($PsqtBuckets -gt 0) { $extra += "--psqt-buckets"; $extra += "$PsqtBuckets" }
    if (-not [double]::IsNaN($InOffset))   { $extra += "--in-offset";   $extra += "$InOffset" }
    if (-not [double]::IsNaN($InScaling))  { $extra += "--in-scaling";  $extra += "$InScaling" }
    if (-not [double]::IsNaN($OutOffset))  { $extra += "--out-offset";  $extra += "$OutOffset" }
    if (-not [double]::IsNaN($OutScaling)) { $extra += "--out-scaling"; $extra += "$OutScaling" }
    python train_nnue.py `
        --data @shards `
        --out "checkpoints\$Name.pt" `
        --epochs $Epochs `
        --batch 16384 `
        --lambda 0.85 `
        --lr 0.001 `
        --weight-decay 1e-5 `
        --val-fraction 0.05 `
        --seed 1 `
        --ft-out $FtOut `
        --l1-out 32 `
        --out-buckets $OutBuckets `
        @extra
    if ($LASTEXITCODE -ne 0) { throw "training failed with exit code $LASTEXITCODE" }

    Write-Host "`n=== validate (a sanity check, NOT a strength measurement) ==="
    # ds2 matched the baseline on every training metric - loss 0.005358 vs
    # 0.005301, correlation 0.9396 vs 0.9402 - and then lost by 108 Elo. These
    # numbers catch a broken run. They do not rank two working ones.
    python validate_nnue.py --checkpoint "checkpoints\$Name.pt" --data $shards[0]

    Write-Host "`n=== export ==="
    $model = Join-Path $repo "models\nnue\noa-$Name.noannue"
    python export_model.py --checkpoint "checkpoints\$Name.pt" --arch $Arch --out $model
    if ($LASTEXITCODE -ne 0) { throw "export failed" }

    Write-Host "`n=== verify the export reproduces the engine's integer forward pass ==="
    python verify_export.py --model $model `
        --fen "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1" `
        --fen "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1" `
        --fen "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1"
}
finally {
    Pop-Location
}

Write-Host "`nDone. The net is NOT promoted until an SPRT says so."
Write-Host "Next: F:\Works\_______________CHESSTEST\sprt_$Name`_vs_ds1e60.bat"
