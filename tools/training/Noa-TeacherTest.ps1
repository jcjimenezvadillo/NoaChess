# Does a stronger TEACHER produce a stronger net? Answered in one night, not
# three days.
#
# THE QUESTION. Every one of the 324,297,032 positions in the corpus was labelled
# by noa-gen7, which plays around 3080. The net trained on it now measures 3271.
# The student is roughly 200 Elo above its teacher, and every label it learns
# from is that teacher's opinion.
#
# WHY IT IS NOT OBVIOUS. This project already ran five generations of exactly
# this idea and they landed flat: gen3 +4.5, gen4 +1.9, gen5 +34, gen6 failed,
# gen7 level with gen5. That produced the conclusion "self-play is exhausted",
# which is written into the ROADMAP and the README.
#
# WHY IT IS WORTH ASKING AGAIN ANYWAY. That conclusion was reached with a
# BROKEN TRAINER. 85.6% of the feature transformer was quantising to zero, so a
# generation could look flat because the net could not use better labels, not
# because better labels do not exist. Feature factorization fixed that and was
# worth +195.4. The experiment that closed this axis is no longer valid.
#
# THE DESIGN. One axis, and the control costs nothing:
#   arm A  a fresh corpus of the SAME size, laballed by fq60 at the same 6000 nodes
#   arm B  a slice of the EXISTING corpus, same size, labelled by gen7
# Both trained with identical settings, then played against each other. The only
# difference between the two nets is who labelled their data.
#
# Cost: about 5 h of datagen plus two short trainings, against the 3+ days a full
# regeneration of 324M positions would take. Precedent: the phase-0 calibration
# answered a 3-day question in 4 hours and changed the whole campaign.

param(
    # 20M is the size the data-scale calibration used, and it resolved a +182
    # Elo difference, so it is known to be enough to see an effect this size.
    [long]$Positions = 20000000,

    # The teacher under test. Anything here must be a net that already has a
    # measured strength, or the result cannot be attributed.
    [string]$Teacher = "models\nnue\noa-fq60.noannue",

    # Same as the corpus being compared against. Changing it would confound the
    # teacher's strength with the depth it was allowed to think.
    [int]$Nodes = 6000,

    [int]$Threads = 24,
    [int]$Epochs = 60,

    # C: is the SSD, F: is a mechanical disk, so every shard the trainer reads
    # in a loop belongs here. The 324M corpus already lives on C:\NoaData for
    # this reason; the first run of this script wrote its own shards to F: and
    # left one arm reading from SSD and the other from spinning rust.
    [string]$Out = "C:\NoaData\teachertest"
)

$ErrorActionPreference = "Stop"
$repo = "F:\Works\Programacion\__Repos\NoaChess"
$test = "F:\Works\_______________CHESSTEST"
$out  = $Out
$gen  = Join-Path $repo "tools\NoaChess.DataGen\bin\Release\net10.0\NoaChess.DataGen.exe"
$train = Join-Path $repo "tools\training\nnue"
$log  = Join-Path $repo "logs\teachertest-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
New-Item -ItemType Directory -Force $out | Out-Null

function Say($t) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $t
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path $gen)) { throw "no existe $gen - compila NoaChess.DataGen en Release" }
if (-not (Test-Path (Join-Path $repo $Teacher))) { throw "no existe el profesor $Teacher" }

Say "profesor  : $Teacher"
Say "posiciones: $Positions a $Nodes nodos"
Say "salida    : $out"
Say ""

# ---- arm A: a new corpus labelled by the new teacher ----
Say "=== generando el corpus nuevo (unas 5 h) ==="
& $gen --nodes $Nodes --threads $Threads --seed 20260810 `
       --model (Join-Path $repo $Teacher) `
       --positions $Positions --shard-size 5000000 `
       --out (Join-Path $out "newteacher.noadata") 2>&1 | Tee-Object -FilePath $log -Append
if ($LASTEXITCODE -ne 0) { throw "datagen fallo con codigo $LASTEXITCODE" }

$shardsA = @(Get-ChildItem $out -File -Filter "newteacher*.noadata" | Sort-Object Name |
             ForEach-Object { $_.FullName })
Say "shards del profesor nuevo: $($shardsA.Count)"

# ---- arm B: the same amount of the OLD corpus, labelled by gen7 ----
# Taken from the front of the existing shard list, which is the same mix by
# construction: the corpus was written shard by shard from the same generator.
$old = @(Get-ChildItem "C:\NoaData\datascale" -File -Filter "*.noadata" | Sort-Object Name)
$shardsB = @()
$acc = 0L
foreach ($f in $old) {
    $shardsB += $f.FullName
    $acc += [math]::Floor(($f.Length - 64) / 40)
    if ($acc -ge $Positions) { break }
}
Say "shards del profesor viejo: $($shardsB.Count) (~$acc posiciones)"

Push-Location $train
try {
    foreach ($arm in @(@{n="tnew"; s=$shardsA}, @{n="told"; s=$shardsB})) {
        Say ""
        Say "=== entrenando $($arm.n) ==="
        python train_nnue.py --data @($arm.s) --out "checkpoints\$($arm.n).pt" `
            --epochs $Epochs --batch 16384 --lambda 0.85 --lr 0.001 `
            --weight-decay 1e-5 --val-fraction 0.05 --seed 1 `
            --ft-out 128 --l1-out 32 --out-buckets 1 --factorized --qat --qa 255 2>&1 |
            Tee-Object -FilePath $log -Append
        if ($LASTEXITCODE -ne 0) { throw "el entrenamiento de $($arm.n) fallo" }
        python export_model.py --checkpoint "checkpoints\$($arm.n).pt" --arch 1 `
            --out (Join-Path $repo "models\nnue\noa-$($arm.n).noannue") 2>&1 |
            Tee-Object -FilePath $log -Append
    }
}
finally { Pop-Location }

# ---- the comparison ----
$bat = Join-Path $test "sprt_tnew_vs_told.bat"
(Get-Content (Join-Path $test "sprt_fact60_vs_ds1e60.bat") -Raw) `
    -replace 'noa-fact60\.noannue', 'noa-tnew.noannue' `
    -replace 'noa-ds1e60\.noannue', 'noa-told.noannue' `
    -replace 'name=fact60', 'name=tnew-fq60teacher' `
    -replace 'name=ds1e60', 'name=told-gen7teacher' `
    -replace 'sprt_fact60_vs_ds1e60\.pgn', 'sprt_tnew_vs_told.pgn' |
    Set-Content -Path $bat -Encoding ASCII

Say ""
Say "Listo. Las dos redes solo se diferencian en QUIEN etiqueto sus datos."
Say "Ejecuta: $bat"
Say "  gana tnew  -> el profesor importa, y regenerar los 324M vale los 3 dias"
Say "  empata     -> el profesor NO era el cuello de botella, y te ahorras 3 dias"
