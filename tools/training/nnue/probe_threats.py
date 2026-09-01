# The 2x2 that decides whether threat features are worth weeks of engine work.
#
# THE QUESTION IT ANSWERS, AND THE ONE IT DOES NOT. Adding threats to a 128-wide
# transformer and getting nothing back would not tell us whether the features are
# useless or merely do not fit, because width has already been measured as a
# loser on the CURRENT input (fqw256 at -31.9) and a richer input is exactly the
# thing that could change that answer. So the arms are crossed:
#
#              width 128        width 256
#   HalfKA     baseline         width alone
#   +threats   threats alone    both
#
# Read it as a table, not as four numbers. If threats gain at both widths, they
# are worth building. If they gain only at 256, the input and the capacity are
# coupled and the engine work has to carry a width change with it. If they gain
# at neither, the idea is dead and no C# was written to find out.
#
# WHAT IS BEING COMPARED. Validation loss on held-out positions, which is the
# same quantity the real trainer reports, plus the correlation with the teacher's
# own evaluation. Neither is Elo. A gain here is permission to build the thing
# and measure Elo properly; it is not a strength claim.
import argparse
import subprocess
import sys
import time

import numpy as np
import torch
import torch.nn as nn

import dataset
import threats

# HalfKA's own factorization, as the trainer does it: 704 virtual (piece,
# square) rows, one per real feature with the king bucket dropped, so each
# collects 32 real features' worth of gradient.
HALFKA_VIRTUAL = 704
MAX_KA_ACTIVE = 32 * 2                    # 32 real plus one virtual each
MAX_TH_ACTIVE = 128 * 4                   # up to 128 real plus three virtuals each


def cap_gpu_memory(fraction):
    """Hard ceiling on this process's share of the card.

    Only used when deliberately running beside a training run. The card has 16
    GB and a training of this shape sits around 2, so memory is not the scarce
    thing - but "not scarce" is a measurement of this minute, and an allocator
    that grows without a ceiling can still reach across and end an eighteen-hour
    run with an out-of-memory. A quarter of the card is several times what the
    2x2 needs and cannot starve anything.
    """
    if fraction and torch.cuda.is_available():
        torch.cuda.set_per_process_memory_fraction(fraction)
        total = torch.cuda.get_device_properties(0).total_memory / 2**30
        print(f"  GPU limitada a {fraction:.0%} de {total:.1f} GB = {fraction*total:.1f} GB")


def refuse_to_share_the_gpu(force):
    """Stop if a training run already owns the machine.

    Written after running this file as a smoke test while fqc120 was 21 epochs
    into an 18-hour run. Nothing broke that time, but two jobs on one GPU can
    end a training with an out-of-memory hours in, and the cost of finding out
    is the whole run. The guard lives here rather than in the .bat so it holds
    however the file is launched.
    """
    if force:
        return
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq python.exe"],
                             capture_output=True, text=True, timeout=30).stdout
    except Exception:
        return                      # no tasklist, no opinion
    others = out.lower().count("python.exe") - 1      # this process is one of them
    if others > 0:
        print(f"ABORTADO: hay {others} proceso(s) python mas en la maquina.")
        print("Una sonda que comparte GPU con un entrenamiento puede tumbarlo.")
        print("Espera a que termine, o pasa --force si sabes lo que haces.")
        sys.exit(1)


class Net(nn.Module):
    """The shipping shape, with an optional second input bag bolted on.

    Deliberately the same architecture as the real net apart from the extra
    features, because the point is to measure the features and not a redesign.
    """

    def __init__(self, ft_out, with_threats, factor_halfka=True):
        super().__init__()
        # Both feature sets are FACTORIZED, and both arms get it. Factorization
        # is worth +128 Elo in this project and exists to cure exactly one
        # thing: a sparse row starving for gradient. Leaving it out handicapped
        # the threat arm specifically, because threats are 60,720 dimensions
        # against HalfKA's 22,528 with about half the updates per weight - so
        # the first run of this probe tested the idea with its characteristic
        # defect left in and unsurprisingly reported it lost.
        self.halfka = nn.EmbeddingBag(dataset.INPUT_SIZE + HALFKA_VIRTUAL + 1, ft_out, mode="sum")
        self.with_threats = with_threats
        self.factor_halfka = factor_halfka
        if with_threats:
            self.threats = nn.EmbeddingBag(threats.FACTORED_INPUT_SIZE + 1, ft_out, mode="sum")
        width = 2 * ft_out
        self.l1 = nn.Linear(width, 32)
        self.out = nn.Linear(32, 1)

    def side(self, halfka_idx, threat_idx):
        # The control arm drops the virtual half of the HalfKA indices, which is
        # exactly "unfactorized". Nothing else changes, so the difference the
        # probe reports between the two is the factorization and only that.
        if not self.factor_halfka:
            halfka_idx = halfka_idx[:, :MAX_KA_ACTIVE // 2]
        acc = self.halfka(halfka_idx)
        if self.with_threats:
            acc = acc + self.threats(threat_idx)
        return torch.clamp(acc, 0.0, 1.0)

    def forward(self, stm_ka, opp_ka, stm_th, opp_th):
        both = torch.cat([self.side(stm_ka, stm_th), self.side(opp_ka, opp_th)], dim=1)
        return self.out(torch.clamp(self.l1(both), 0.0, 1.0))


def load(paths, limit):
    """Records, HalfKA features and threat features for the same positions."""
    chunks, taken = [], 0
    for path in paths:
        recs = dataset.load_records(path)          # memory-mapped, not read
        want = min(limit - taken, len(recs))
        chunks.append(np.array(recs[:want]))
        taken += want
        if taken >= limit:
            break
    arr = np.concatenate(chunks) if len(chunks) > 1 else chunks[0]
    print(f"  {len(arr):,} positions from {len(chunks)} shard(s)")

    ka_stm, ka_opp, scores, results = dataset.decode_block(arr)

    # HalfKA factorization: every real feature also fires its (piece, square)
    # row with the king bucket dropped, which is real_index % 704.
    def factor_ka(block):
        out = np.full((len(block), MAX_KA_ACTIVE), -1, dtype=np.int32)
        real = block.shape[1]
        out[:, :real] = block
        virtual = np.where(block < 0, -1, dataset.INPUT_SIZE + (block % HALFKA_VIRTUAL))
        out[:, real:real + real] = virtual
        return out

    ka_stm, ka_opp = factor_ka(ka_stm), factor_ka(ka_opp)

    # The threat side, one position at a time on verified primitives.
    started = time.time()
    th_stm = np.full((len(arr), MAX_TH_ACTIVE), -1, dtype=np.int32)
    th_opp = np.full((len(arr), MAX_TH_ACTIVE), -1, dtype=np.int32)
    overflow = 0
    for i, rec in enumerate(arr):
        white, black = threats.active_threats(int(rec["occupancy"]), rec["pieces"])
        a, b = (white, black) if int(rec["stm"]) == 0 else (black, white)
        a, b = threats.factorize(a), threats.factorize(b)
        # A silent truncation would be a dropped feature rather than a crash, so
        # it gets counted and reported instead of quietly changing the answer.
        if len(a) > MAX_TH_ACTIVE or len(b) > MAX_TH_ACTIVE:
            overflow += 1
        th_stm[i, : min(len(a), MAX_TH_ACTIVE)] = a[:MAX_TH_ACTIVE]
        th_opp[i, : min(len(b), MAX_TH_ACTIVE)] = b[:MAX_TH_ACTIVE]
        if i and i % 200000 == 0:
            print(f"    threats {i:,}/{len(arr):,}  ({time.time() - started:.0f}s)", flush=True)
    print(f"  threat encoding took {time.time() - started:.0f}s")
    if overflow:
        print(f"  AVISO: {overflow:,} posiciones pasaron de {MAX_TH_ACTIVE} indices y se truncaron")

    pad_ka = dataset.INPUT_SIZE + HALFKA_VIRTUAL
    pad_th = threats.FACTORED_INPUT_SIZE
    return (torch.from_numpy(np.where(ka_stm < 0, pad_ka, ka_stm).astype(np.int64)),
            torch.from_numpy(np.where(ka_opp < 0, pad_ka, ka_opp).astype(np.int64)),
            torch.from_numpy(np.where(th_stm < 0, pad_th, th_stm).astype(np.int64)),
            torch.from_numpy(np.where(th_opp < 0, pad_th, th_opp).astype(np.int64)),
            torch.from_numpy(scores), torch.from_numpy(results))


def run(arm, data, ft_out, with_threats, epochs, batch, lam, device, factor_halfka=True):
    ka_s, ka_o, th_s, th_o, scores, results = data
    n = len(scores)
    cut = int(n * 0.95)

    torch.manual_seed(1)                      # same start for every arm
    net = Net(ft_out, with_threats, factor_halfka).to(device)
    opt = torch.optim.Adam(net.parameters(), lr=1e-3, weight_decay=1e-5)

    # THE FIX THAT MADE THE FIRST RUN WORTHLESS. Without a schedule and with only
    # twelve epochs, all four arms were cut off mid-descent, and the answer was
    # decided by which one converged FASTER rather than which one ends up better.
    # That favours the smaller arm by construction: the threat embedding is
    # 60,720 dimensions against HalfKA's 22,528 and sees about 1,300 updates per
    # weight at 2M positions where HalfKA sees 2,800, so it learns roughly a
    # third slower and finished the run three or four epochs behind. Exactly the
    # error already diagnosed in fqc60 the same morning: measuring where the
    # training stopped rather than where the learning stopped.
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=epochs, eta_min=1e-5)

    def target(idx):
        wdl = torch.sigmoid(scores[idx] / 410.0)
        return lam * wdl + (1 - lam) * (results[idx] * 0.5 + 0.5)

    best = float("inf")
    best_epoch = 0
    history = []
    for epoch in range(1, epochs + 1):
        net.train()
        order = torch.randperm(cut)
        for begin in range(0, cut, batch):
            idx = order[begin: begin + batch]
            pred = torch.sigmoid(net(ka_s[idx].to(device), ka_o[idx].to(device),
                                     th_s[idx].to(device), th_o[idx].to(device)).squeeze(1))
            loss = ((pred - target(idx).to(device)) ** 2).mean()
            opt.zero_grad(); loss.backward(); opt.step()

        net.eval()
        with torch.no_grad():
            vs, vp = [], []
            for begin in range(cut, n, batch):
                idx = torch.arange(begin, min(begin + batch, n))
                p = torch.sigmoid(net(ka_s[idx].to(device), ka_o[idx].to(device),
                                      th_s[idx].to(device), th_o[idx].to(device)).squeeze(1))
                vp.append(p.cpu()); vs.append(target(idx))
            vp, vs = torch.cat(vp), torch.cat(vs)
            val = ((vp - vs) ** 2).mean().item()
            corr = float(np.corrcoef(vp.numpy(), vs.numpy())[0, 1])
        sched.step()
        if val < best:
            best, best_epoch = val, epoch
        history.append(val)
        print(f"  [{arm}] epoch {epoch:2d}  val {val:.6f}  corr {corr:.4f}  "
              f"lr {opt.param_groups[0]['lr']:.2e}", flush=True)

    # Whether the arm actually finished is part of the result, not a footnote. An
    # arm still improving in its last tenth was cut off, and a comparison between
    # a converged arm and a truncated one measures the truncation.
    tail = (history[-max(2, epochs // 10)] - history[-1]) / history[-1] * 100
    print(f"  [{arm}] mejor {best:.6f} en la epoca {best_epoch}; "
          f"ultimo 10% de epocas mejoro {tail:.2f}%"
          f"{'  <- SEGUIA BAJANDO, no ha convergido' if tail > 0.5 else '  (aplanado)'}")

    return best, corr, tail


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", nargs="+", required=True)
    ap.add_argument("--positions", type=int, default=2_000_000)
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--batch", type=int, default=16384)
    ap.add_argument("--lam", type=float, default=0.85)
    ap.add_argument("--force", action="store_true",
                    help="run even if another python process owns the machine")
    ap.add_argument("--gpu-fraction", type=float, default=None,
                    help="ceiling on this process's GPU memory; defaults to 0.25 under --force")
    # The width axis has to reach where the reference actually lives. Its
    # transformer is 1024 wide and these features were designed against that;
    # ours is 128. Feeding 60,720 extra input dimensions through a bottleneck
    # eight times narrower and concluding "the features do not help" would be a
    # statement about the bottleneck, not about the features. Width already
    # measured as a LOSER on our current input (-30.3 at 256), so a richer input
    # flipping that sign is the specific thing worth knowing.
    ap.add_argument("--widths", type=int, nargs="+", default=[128, 256, 512, 1024])
    args = ap.parse_args()

    refuse_to_share_the_gpu(args.force)

    device = "cuda" if torch.cuda.is_available() else "cpu"
    # 0.55 under --force, not 0.25. The first setting was chosen to be obviously
    # safe and turned out to be obviously too tight: the 1024-wide arm asked for
    # 3.44 GiB with 9.42 GiB free on the card and was refused, because the cap
    # allowed 5.60 GiB and PyTorch had already reserved most of it. The guard did
    # its job - the training beside it finished the hour without a hiccup - but a
    # cap that kills the measurement it was protecting has the balance wrong.
    # Just over half the card still leaves a training run more than it uses.
    fraction = args.gpu_fraction if args.gpu_fraction is not None else (0.55 if args.force else None)
    cap_gpu_memory(fraction)
    print(threats.describe())
    print(f"device: {device}")
    print("loading:")
    data = load(args.data, args.positions)

    # THE POSITIVE CONTROL, and it runs first on purpose.
    #
    # A test that reports "no effect" is worthless until it has shown, in the
    # same run, that it can detect an effect that is known to be real. Feature
    # factorization is worth +128 Elo in the field here, measured. So the probe
    # first measures factorization against no factorization at the narrowest
    # width: if it cannot see THAT, it cannot see anything, and any negative it
    # goes on to report about threats says nothing.
    #
    # Written after a negative threat result was nearly accepted twice on a
    # probe that had two design defects in it.
    control_width = min(args.widths)
    print(f"=== CONTROL POSITIVO: factorizacion de HalfKA a ft{control_width}")
    print("    mide algo YA MEDIDO como ganancia (+128 Elo en el campo).")
    print("    Si la sonda no lo detecta, la sonda no detecta nada.")
    plain = run(f"ft{control_width}-sin-factorizar", data, control_width, False,
                args.epochs, args.batch, args.lam, device, factor_halfka=False)
    factored = run(f"ft{control_width}-factorizado", data, control_width, False,
                   args.epochs, args.batch, args.lam, device, factor_halfka=True)
    control_gain = 100 * (plain[0] - factored[0]) / plain[0]

    # THE CONTROL IS ALSO THE RULER, and that is the more useful half of it.
    # Factorization measured +128 Elo in the field. If it moves validation loss
    # by X% here, then X% is what +128 Elo looks like on this scale, and every
    # other delta can be read in those units instead of as a bare percentage
    # that means nothing on its own.
    #
    # The conversion is crude and only holds locally - validation loss is not
    # linear in Elo and this is one point, not a curve. It is a sense of scale,
    # never a prediction, and it does not get quoted as one.
    CONTROL_ELO = 128.0

    # A floor, because "greater than zero" would let pure noise certify the
    # probe. Below this the two arms are indistinguishable and the run is
    # under-powered rather than informative.
    SENSITIVITY_FLOOR = 0.20
    sensitive = control_gain >= SENSITIVITY_FLOOR

    print(f"    la factorizacion mueve la validacion {control_gain:+.2f}%")
    if sensitive:
        print(f"    SONDA SENSIBLE. Regla de conversion aproximada:"
              f" {control_gain:.2f}% ~ {CONTROL_ELO:.0f} Elo")
    else:
        print(f"    SONDA CIEGA: {control_gain:+.2f}% no llega al minimo de"
              f" {SENSITIVITY_FLOOR}%, asi que no distingue una ganancia de +128 Elo")
        print("    del ruido. Cualquier negativo suyo sobre las amenazas es inservible.")
    print()

    results = {}
    for ft_out in args.widths:
        for with_threats in (False, True):
            arm = f"ft{ft_out}{'+threats' if with_threats else ''}"
            print(f"=== {arm}")
            results[arm] = run(arm, data, ft_out, with_threats,
                               args.epochs, args.batch, args.lam, device)

    print()
    print("                 val loss     corr    cola")
    for arm, (val, corr, tail) in results.items():
        print(f"  {arm:16s} {val:.6f}   {corr:.4f}  {tail:+.2f}%")
    print()
    deltas = {}
    for w in args.widths:
        base = results[f"ft{w}"][0]
        deltas[w] = 100 * (base - results[f"ft{w}+threats"][0]) / base
        scale = (f"  ~ {deltas[w] / control_gain * CONTROL_ELO:+.0f} Elo en unidades del control"
                 if sensitive else "")
        print(f"  amenazas a {w:5d}: {deltas[w]:+.2f}%{scale}")
    if sensitive:
        print()
        print("  Las cifras en Elo son una ESCALA, no un pronostico: salen de un solo")
        print("  punto de conversion y la perdida de validacion no es lineal en Elo.")
    print()

    # The verdict is refused when any arm is still descending, because then the
    # comparison is between rates of convergence and not between destinations.
    truncated = [arm for arm, (_, _, tail) in results.items() if tail > 0.5]
    if truncated:
        print("  SIN VEREDICTO: estos brazos seguian bajando al terminar:")
        for arm in truncated:
            print(f"    {arm}")
        print("  Comparar un brazo cortado con uno convergido mide el corte, no la idea.")
        print("  Sube --epochs hasta que la cola de todos baje del 0,5%.")
        return

    # A blind probe is not allowed to bury anything.
    if not sensitive:
        print("  SIN VEREDICTO: el control positivo FALLO. Esta sonda no detecta")
        print(f"  ni la factorizacion, que vale +128 Elo medidos. Su negativo sobre")
        print("  las amenazas no significa nada. Arregla la sonda antes de leerla.")
        return

    winners = [w for w in args.widths if deltas[w] > 0]
    if len(winners) == len(args.widths):
        print("  GANAN A TODAS LAS ANCHURAS: construirlo.")
    elif winners:
        print(f"  Ganan a partir de {min(winners)} y no por debajo: la entrada y la")
        print("  capacidad van ACOPLADAS. Las features llevan informacion, pero el")
        print("  motor tendria que llevarse un cambio de anchura con ellas, y la")
        print("  anchura ya se midio como perdedora sobre la entrada pobre.")
    else:
        print("  No ganan a ninguna anchura, incluida la de la referencia, y con")
        print("  todos los brazos convergidos y factorizados. Antes de enterrarlo,")
        print("  la unica diferencia que queda con la referencia es la ESCALA DE")
        print("  DATOS: ellos entrenan con miles de millones de posiciones.")


if __name__ == "__main__":
    main()
