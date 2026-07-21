@echo off
REM ============================================================
REM  v2.0 promotion gate: NNUE candidate vs Classical baseline.
REM  SAME engine binary; only the evaluator options differ, so the
REM  measured difference is exactly the evaluation change.
REM
REM  SPRT H0=0 / H1=+10 Elo: stops automatically when there is
REM  statistical evidence. "H1 accepted" -> promote the model.
REM
REM  Adjust ENGINE and MODEL paths before running.
REM ============================================================
set ENGINE=F:\Works\_______________CHESSTEST\builds\NoaChess-2.0.0\NoaChess.UCI.exe
set MODEL=F:\Works\Programacion\__Repos\NoaChess\models\nnue\noa-v2.noannue

cd /d F:\Works\_______________CHESSTEST\
if not exist results mkdir results

cutechess\cutechess-cli.exe ^
  -engine name=NoaChess-NNUE cmd=%ENGINE% proto=uci ^
    option.UseNNUE=true option.EvalFile=%MODEL% ^
  -engine name=NoaChess-Classical cmd=%ENGINE% proto=uci ^
  -each tc=10+0.1 timemargin=200 option.Hash=64 ^
  -rounds 2000 -games 2 -repeat ^
  -sprt elo0=0 elo1=10 alpha=0.05 beta=0.05 ^
  -openings file=books\8moves_v3.pgn order=random ^
  -draw movenumber=40 movecount=8 score=10 ^
  -resign movecount=4 score=800 ^
  -concurrency 4 ^
  -recover ^
  -ratinginterval 20 ^
  -pgnout results\sprt_nnue_vs_classical.pgn
pause
