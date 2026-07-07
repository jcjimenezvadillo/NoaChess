@echo off
REM ============================================================
REM  NoaChess NNUE overnight training - run4 "reinforcement pass"
REM
REM  Difference vs run3: self-play now uses the run3 NNUE model
REM  instead of the classical evaluator (--model flag), so this
REM  generation of data is labeled by the network we just trained,
REM  not by the hand-written eval. Also more games to fill the
REM  ~6h budget.
REM
REM  Pipeline: datagen -> train -> validate -> export.
REM  Leave it running; every stage prints progress and the next
REM  one starts automatically.
REM
REM  Output when finished:
REM    data\run4.noadata            (+ .manifest.json)
REM    tools\training\nnue\checkpoints\run4.pt
REM    models\nnue\noa-v2-run4.noannue   <- the engine model
REM ============================================================
cd /d F:\Works\Programacion\__Repos\NoaChess

echo.
echo === STEP 1/4: Self-play data generation (350K games, 15K nodes, NNUE run3) ===
echo     This is the long step. ETA prints every 50 games.
echo.
REM 26 of the machine's 32 cores: datagen scales linearly with threads.
dotnet run --project tools/NoaChess.DataGen -c Release -- ^
  --games 350000 --nodes 15000 --threads 26 --seed 200 ^
  --model models/nnue/noa-v2-run3.noannue --out data/run4.noadata
if errorlevel 1 goto :failed

echo.
echo === STEP 2/4: Training (4 epochs, lambda 0.7) ===
echo.
cd tools\training\nnue
python train_nnue.py --data ../../../data/run4.noadata ^
  --out checkpoints/run4.pt --epochs 4 --batch 8192 --lr 1e-3 --lambda 0.7
if errorlevel 1 goto :failed

echo.
echo === STEP 3/4: Validation (loss + quantization error) ===
echo.
python validate_nnue.py --checkpoint checkpoints/run4.pt ^
  --data ../../../data/run4.noadata --samples 20000
if errorlevel 1 goto :failed

echo.
echo === STEP 4/4: Export to engine format ===
echo.
python export_model.py --checkpoint checkpoints/run4.pt ^
  --out ../../../models/nnue/noa-v2-run4.noannue
if errorlevel 1 goto :failed

echo.
echo ============================================================
echo  ALL DONE. Model: models\nnue\noa-v2-run4.noannue
echo  Next: informal match vs run3 and vs classical, then SPRT.
echo ============================================================
pause
exit /b 0

:failed
echo.
echo ############################################################
echo  A STEP FAILED - check the output above. Nothing after the
echo  failing step was executed.
echo ############################################################
pause
exit /b 1
