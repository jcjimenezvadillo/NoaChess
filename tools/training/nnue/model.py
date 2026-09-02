# NoaChess NNUE architecture id 1 (mirror of the C# inference math in
# NnueInference.cs). Trained in float; quantization happens at export.
#
#   feature transformer: 22528 (HalfKAv2_hm) -> FT_OUT per perspective (shared)
#   activation: clipped ReLU to [0, 1]
#   hidden: concat(stm, opp) 2*FT_OUT -> L1_OUT, clipped ReLU
#   output: L1_OUT -> 1  (units: centipawns / OUTPUT_SCALE)

import torch
import torch.nn as nn
import torch.nn.functional as F

from dataset import INPUT_SIZE, PS_NB, KING_BUCKET_COUNT, MAX_ACTIVE  # schema, single source.
import threats as threats_mod  # threat feature schema, single source for arch 4.

FT_OUT = 128
L1_OUT = 32
OUTPUT_SCALE = 400.0  # net output * 400 = centipawns

# Second hidden layer width (architecture 5 only). Ignored when dual is off.
L2_OUT = 32

# ARCHITECTURE 5 (v5.2.0): the head rebuilt to compute what a modern reference
# evaluation computes. Arch 1-4 are ONE linear layer with ONE clipped ReLU on
# top of the accumulator - that single clamp is the entire non-linearity of the
# evaluation, and no amount of extra data or width can make a shallow clipped
# linear map express a term like "this knight is good BECAUSE that file is
# open". Three changes, none of which needs a wider transformer:
#
#   1. PAIRWISE TRANSFORMER READ. The accumulator is split in half and
#      multiplied element by element instead of being clipped and passed on:
#          pair[j] = clamp(x[j], 0, 1) * clamp(x[j + H], 0, 1)
#      That is a genuine second-order interaction between features at the very
#      first layer, and it HALVES the L1 input width - so the head gets
#      stronger and cheaper at the same time.
#   2. DUAL ACTIVATION. Each hidden layer emits its clipped activation AND the
#      square of it, so the next layer sees a second-order term per unit for
#      one multiply.
#   3. SECOND HIDDEN LAYER WITH A SKIP. The output reads BOTH layers'
#      activations, so the second layer only has to learn what the first could
#      not express, and the first layer's signal is never bottlenecked.
#
# Plus a linear bypass: the last two L1 PRE-activations enter the output
# directly as (pre[-2] - pre[-1]), giving the network two units that carry an
# unbounded linear score past every clamp.
#
# OFF by default, like buckets and factorization and for the same reason: it
# changes the shape of l1 and out, so it must be an explicit opt-in rather than
# something that silently invalidates every existing checkpoint.
DUAL = False

# The engine divides the pairwise product by 128 with a SHIFT, not by QA: there
# is no cheap exact SIMD division by 127, and the obvious reciprocal-multiply
# tricks are off by one for inputs of the form 127k-1. That shift is part of the
# contract, so the float model carries the same constant and the two sides
# describe ONE function:
#     engine  pair_int = (a0 * a1) >> 7          with a = round(x * QA)
#     trainer pair     = x0 * x1 * QA / 128
# so that pair * QA == pair_int exactly (up to the floor the QAT path models).
PAIR_DIVISOR = 128.0

# Output buckets (architecture 3, v4.2.0). The head is replicated per bucket and
# the bucket is chosen from the piece count, so the network gets a specialised
# readout per phase instead of one linear map serving a 32-piece opening and a
# 4-piece ending alike. Only ONE bucket is evaluated at play time, so this buys
# capacity at essentially zero runtime cost - the best trade in the architecture,
# which is why it lands before any width increase.
#
# DEFAULT IS 1, DELIBERATELY. Buckets are an explicit opt-in (--out-buckets 8),
# not a silent change of what every existing caller produces. Defaulting to 8
# meant a script that had always trained an unbucketed net suddenly produced a
# bucketed checkpoint, which then failed at export against --arch 1 - after the
# training had already run. A default that quietly changes existing output is a
# trap, and the failure surfaces hours downstream of the cause.
OUT_BUCKETS = 1

# Feature factorization (v4.6.0). OFF by default for the same reason buckets are:
# it changes the shape of ft.weight, so it must be an explicit opt-in.
#
# THE PROBLEM IT SOLVES, MEASURED ON THE SHIPPING NET. A HalfKAv2_hm index is
# (king bucket, piece, square), so each of the 22,528 features only ever fires
# when the king sits in one of 32 regions. Individually they are rare, their
# weights never grow, and in ds1e60 89.8% of the feature transformer quantizes
# to exactly ZERO at QA=127, with 2,389 whole features (10.6%) dead. The largest
# weight reaches 26 of the 127 available. The transformer is barely used.
#
# THE FIX. Train with 704 extra VIRTUAL features - the king-independent
# (piece, square) part that each real feature is a copy of. Every active feature
# fires its real row AND its virtual row, so the virtual row collects 32x the
# gradient and carries the bulk of the signal while the real rows learn only the
# per-king-bucket deviation. At export the virtual row is ADDED into each of its
# 32 copies, which is exact: the sum the accumulator computes is unchanged.
#
# The engine therefore needs no change at all. The exported file has the same
# 22,528 rows, the same header and the same arithmetic; the virtual features
# exist only while gradients are flowing.
FACTORIZED = False

# Quantization scales (must match the C# loader and export_model.py).
QA = 255
QB = 64

# Quantization-aware training (v4.6.0). OFF by default, opt in with --qat.
#
# THE PROBLEM IT SOLVES, MEASURED ON THE SHIPPING NET. Quantizing each stage
# alone against the float forward pass over 4,000 real positions:
#     feature transformer only   38.77 cp mean absolute error
#     head (L1 + output) only     4.9  cp
# against a mean absolute evaluation of 231 cp. The engine therefore evaluates
# 16.6% away from the network that was trained, p95 98 cp, and essentially all
# of it is the feature transformer. Training in float and rounding afterwards
# optimises a network the engine never runs.
#
# THE FIX. Round the weights inside the forward pass and let the gradient pass
# through unchanged (straight-through estimator), so the optimiser sees the
# error the rounding causes and learns weights that survive it.
#
# TWO DIFFERENT ROUNDINGS, AND THE DISTINCTION IS NOT COSMETIC:
#   weights      -> round(), because export rounds them
#   activations  -> floor(), because the engine's L1 divides by QB with INTEGER
#                   division, which truncates. Rounding here would train against
#                   arithmetic the engine does not perform, and the bias is
#                   systematic (half a step per hidden unit, same sign every
#                   time), not noise that averages away.
# The epsilon keeps a value already sitting exactly on a grid point from being
# floored down by a representation error.
FAKE_QUANTIZE_EPS = 1e-5


def fake_quantize_weights(value, scale):
    """Rounds to the quantization grid; gradient passes through untouched."""
    hard = value.mul(scale).round().div(scale).detach()
    return hard + (value - value.detach())


def fake_quantize_acts(value, scale):
    """Floors to the grid, mirroring the engine's integer division."""
    hard = value.mul(scale).add(FAKE_QUANTIZE_EPS).floor().div(scale).detach()
    return hard + (value - value.detach())


def bucket_for_piece_count(piece_count, buckets):
    """
    Mirror of NnueModelHeader.BucketForPieceCount. Duplicating this formula is
    how a trainer and an engine drift apart silently, so it lives in exactly one
    place on each side and the tests pin its values on both.
        bucket = clamp((pieceCount - 1) * buckets // 32, 0, buckets - 1)
    """
    if buckets <= 1:
        return torch.zeros_like(piece_count)
    return torch.clamp((piece_count - 1) * buckets // 32, 0, buckets - 1)


class NoaNnue(nn.Module):
    # ft_out / l1_out default to the module constants but can be widened to
    # sweep capacity (256, 512, ...). The C# runtime reads every dimension from
    # the model header, so a wider or more bucketed net needs no engine change
    # - only a retrain and an export.
    def __init__(self, ft_out=FT_OUT, l1_out=L1_OUT, out_buckets=OUT_BUCKETS,
                 factorized=FACTORIZED, qat=False, qa=QA, threats=False,
                 dual=DUAL, l2_out=L2_OUT, psqt_buckets=0, coarse=False):
        super().__init__()
        self.ft_out = ft_out
        self.l1_out = l1_out
        self.dual = bool(dual)
        self.l2_out = l2_out if self.dual else 0
        if self.dual and ft_out % 2 != 0:
            raise ValueError("dual activation pairs the two halves of the "
                             f"accumulator, so ft_out must be even (got {ft_out})")
        self.out_buckets = max(1, out_buckets)
        self.factorized = bool(factorized)
        # qa MUST match the architecture the net will be exported as: 255 for
        # arch 1, 127 for arch 2/3. Training against one and exporting as the
        # other trains for arithmetic the engine will not run.
        self.qat = bool(qat)
        self.qa = qa
        # EmbeddingBag with padding trick: sparse sum of feature rows.
        # Index 0..INPUT_SIZE-1 are real features. When factorized, the next
        # PS_NB rows are the virtual (piece, square) features. The last row is a
        # zero padding row so batches can be rectangular.
        self.virtual_base = INPUT_SIZE
        self.pad_index = INPUT_SIZE + (PS_NB if self.factorized else 0)
        self.ft = nn.EmbeddingBag(self.pad_index + 1, ft_out, mode="sum",
                                  padding_idx=self.pad_index)
        self.ft_bias = nn.Parameter(torch.zeros(ft_out))

        # Psqt head (two-headed net): one linear output per feature per psqt
        # bucket, summed per perspective exactly like the transformer, read at
        # evaluation as (psqt_stm - psqt_opp) / 2 in RAW output units. It is
        # exported as int32 at OUTPUT_SCALE, whose rounding step of 1/400 of a
        # raw unit is far below training noise, so it needs no fake
        # quantization. When the net trains factorized the batches feed real
        # AND virtual indices to every EmbeddingBag, this one included, so its
        # virtual rows train too and export MUST fold them (fold_psqt below,
        # same exactness argument as fold_features).
        self.psqt_buckets = max(0, int(psqt_buckets))
        if self.psqt_buckets > 0:
            if threats or dual:
                raise ValueError("psqt head is not supported with threats or arch 5 yet")
            self.psqt = nn.EmbeddingBag(self.pad_index + 1, self.psqt_buckets,
                                        mode="sum", padding_idx=self.pad_index)
            nn.init.zeros_(self.psqt.weight)

        # THREATS: a second transformer summed into the SAME accumulator, which
        # is why it has no bias of its own - a second constant added to one sum
        # is just a different first constant, and ft_bias already carries it.
        # The engine's arch 4 payload has no threat bias block for the same
        # reason.
        #
        # Factorized like HalfKA is, and for a stronger reason: threats are
        # 60,720 rows against HalfKA's 22,528 with roughly half the gradient
        # updates per weight, so they are the sparser of the two and the ones
        # that suffer most without shared rows. The probe measured that
        # directly - unfactorized it reported threats LOSING 5.43%, factorized
        # and converged it reported them gaining 3.96%.
        # COARSE THREATS: 144 side-relative (attacker class, victim class)
        # buckets summed into the SAME accumulator, exactly the threat lane
        # below with the geometry collapsed. The probe measured +4.14% of
        # validation loss for this encoding - as much as the fine set - and
        # the engine pays it per EVALUATION (popcounts off the bitboards,
        # 1.3 us measured), not per node, which is what killed the fine set
        # at the clock. Not factorized: 144 dense rows need no sharing.
        self.coarse = bool(coarse)
        if self.coarse:
            if threats or dual or psqt_buckets:
                raise ValueError("coarse lane expects the plain single-head recipe")
            self.coarse_pad = 144
            self.coarse_ft = nn.EmbeddingBag(self.coarse_pad + 1, ft_out, mode="sum",
                                             padding_idx=self.coarse_pad)
            nn.init.uniform_(self.coarse_ft.weight, -0.05, 0.05)
            with torch.no_grad():
                self.coarse_ft.weight[self.coarse_pad].zero_()

        self.threats = bool(threats)
        if self.threats:
            self.threat_virtual_base = threats_mod.THREAT_INPUT_SIZE
            self.threat_pad = (threats_mod.FACTORED_INPUT_SIZE if self.factorized
                               else threats_mod.THREAT_INPUT_SIZE)
            self.threat_ft = nn.EmbeddingBag(self.threat_pad + 1, ft_out, mode="sum",
                                             padding_idx=self.threat_pad)
            nn.init.uniform_(self.threat_ft.weight, -0.05, 0.05)
            with torch.no_grad():
                self.threat_ft.weight[self.threat_pad].zero_()

        # The head is bucket-major, matching the C# payload layout exactly:
        # l1 holds buckets * l1_out rows, out holds one row per bucket.
        #
        # ARCH 5 changes two shapes and adds one layer. The L1 input is HALVED
        # because the pairwise read turns 2*ft_out clipped values into ft_out
        # products, and the output row spans both layers' dual activations:
        # 2*l1_out from the first and 2*l2_out from the second.
        if self.dual:
            self.l1 = nn.Linear(ft_out, self.out_buckets * l1_out)
            self.l2 = nn.Linear(2 * l1_out, self.out_buckets * self.l2_out)
            self.out = nn.Linear(2 * l1_out + 2 * self.l2_out, self.out_buckets)
        else:
            self.l1 = nn.Linear(2 * ft_out, self.out_buckets * l1_out)
            self.out = nn.Linear(l1_out, self.out_buckets)

        # Small init keeps the quantized ranges healthy from the start.
        nn.init.uniform_(self.ft.weight, -0.05, 0.05)
        with torch.no_grad():
            self.ft.weight[self.pad_index].zero_()

    def forward(self, stm_feats, opp_feats, stm_threats=None, opp_threats=None,
                stm_coarse=None, opp_coarse=None):
        # The piece count is read off the FEATURES, not carried as a separate
        # column: in HalfKA every piece is exactly one feature, so the number of
        # non-padding entries IS the piece count. That keeps the dataset format
        # unchanged and lets already-decoded shards train a bucketed net.
        piece_count = (stm_feats >= 0).sum(dim=1).long()

        ft_weight, ft_bias = self.ft.weight, self.ft_bias
        l1_weight, l1_bias = self.l1.weight, self.l1.bias
        out_weight, out_bias = self.out.weight, self.out.bias
        if self.qat:
            # Every scale below is the one export_model.py uses for that tensor.
            ft_weight = fake_quantize_weights(ft_weight, self.qa)
            ft_bias = fake_quantize_weights(ft_bias, self.qa)
            l1_weight = fake_quantize_weights(l1_weight, QB)
            l1_bias = fake_quantize_weights(l1_bias, self.qa * QB)
            out_weight = fake_quantize_weights(out_weight, QB)
            out_bias = fake_quantize_weights(out_bias, self.qa * QB)

        threat_weight = self.threat_ft.weight if self.threats else None
        if self.threats and self.qat:
            # Same grid as the HalfKA transformer: both sum into one int16
            # accumulator, so both have to be quantised to the same scale or the
            # sum lands off the grid the engine holds.
            threat_weight = fake_quantize_weights(threat_weight, self.qa)

        coarse_weight = self.coarse_ft.weight if self.coarse else None
        if self.coarse and self.qat:
            # Same argument as the threat lane: one accumulator, one grid.
            coarse_weight = fake_quantize_weights(coarse_weight, self.qa)

        def transform(feats, threat_feats, coarse_feats):
            # The accumulator needs no activation quantization of its own: with
            # the weights and the bias on the 1/qa grid, an integer number of
            # them sums to a grid point exactly, which is what the engine's
            # int16 accumulator holds.
            acc = F.embedding_bag(self._indices(feats), ft_weight, mode="sum",
                                  padding_idx=self.pad_index) + ft_bias
            if self.threats:
                # Summed BEFORE the clamp, into the same accumulator, exactly as
                # NnueAccumulator.Refresh does it. Clamping the two separately
                # and adding afterwards would be a different function.
                #
                # The padding conversion is not optional: the shard cache stores
                # -1 for unused columns, and embedding_bag rejects a negative
                # index outright. HalfKA goes through _indices for the same
                # reason; threats need their own because their pad row is at a
                # different offset.
                idx = torch.where(threat_feats < 0,
                                  torch.full_like(threat_feats, self.threat_pad),
                                  threat_feats).long()
                acc = acc + F.embedding_bag(idx, threat_weight, mode="sum",
                                            padding_idx=self.threat_pad)
            if self.coarse:
                # Same shape as the threat sum: BEFORE the clamp, into the
                # same accumulator, padding converted for the same reason.
                cidx = torch.where(coarse_feats < 0,
                                   torch.full_like(coarse_feats, self.coarse_pad),
                                   coarse_feats).long()
                acc = acc + F.embedding_bag(cidx, coarse_weight, mode="sum",
                                            padding_idx=self.coarse_pad)
            return torch.clamp(acc, 0.0, 1.0)

        stm = transform(stm_feats, stm_threats, stm_coarse)
        opp = transform(opp_feats, opp_threats, opp_coarse)

        if self.dual:
            return self._forward_dual(stm, opp, piece_count,
                                      l1_weight, l1_bias, out_weight, out_bias)

        hidden_pre = F.linear(torch.cat([stm, opp], dim=1), l1_weight, l1_bias)
        if self.qat:
            # The engine computes clip((l1_b + l1_w @ x) // QB, 0, QA): integer
            # division first, then the clamp. Same order here.
            hidden_pre = fake_quantize_acts(hidden_pre, self.qa)
        hidden_all = torch.clamp(hidden_pre, 0.0, 1.0)
        if self.out_buckets == 1:
            raw = F.linear(hidden_all, out_weight, out_bias).squeeze(1)
            if self.psqt_buckets > 0:
                raw = raw + self._psqt_term(stm_feats, opp_feats, piece_count)
            return raw

        # Select this sample's bucket. Every bucket is computed and one is
        # gathered, which is wasteful in training and irrelevant in cost (l1_out
        # is 32), while play time evaluates the selected bucket only.
        batch = hidden_all.shape[0]
        bucket = bucket_for_piece_count(piece_count, self.out_buckets)
        hidden = hidden_all.view(batch, self.out_buckets, self.l1_out)[
            torch.arange(batch, device=hidden_all.device), bucket]
        weight = out_weight[bucket]             # [batch, l1_out]
        bias = out_bias[bucket]                 # [batch]
        raw = (hidden * weight).sum(dim=1) + bias
        if self.psqt_buckets > 0:
            raw = raw + self._psqt_term(stm_feats, opp_feats, piece_count)
        return raw

    def _psqt_term(self, stm_feats, opp_feats, piece_count):
        # Padding handling mirrors the transformer: EmbeddingBag with the same
        # padding_idx ignores the -1-mapped slots. Bucket selection reuses the
        # OUTPUT bucket formula so a future multi-bucket psqt stays aligned
        # with the head the engine indexes.
        pad = self.pad_index
        # .int(): the streaming loader serves indices as int16 (they fit), and
        # embedding_bag_cuda only takes Int/Long. The ft path casts on its own
        # route; this one must too, and the smoke test's tiny file took the
        # in-RAM route and never saw it - the streaming shape is the one that
        # counts.
        stm = self.psqt(stm_feats.where(stm_feats >= 0, torch.full_like(stm_feats, pad)).int())
        opp = self.psqt(opp_feats.where(opp_feats >= 0, torch.full_like(opp_feats, pad)).int())
        if self.psqt_buckets == 1:
            return (stm[:, 0] - opp[:, 0]) / 2
        bucket = bucket_for_piece_count(piece_count, self.psqt_buckets)
        idx = torch.arange(stm.shape[0], device=stm.device)
        return (stm[idx, bucket] - opp[idx, bucket]) / 2

    def _pair(self, activation):
        """Pairwise product of the two halves of one perspective's accumulator.

        The QA/128 factor is not cosmetic and not a fudge: it is the engine's
        shift, written on this side of the contract. Without it the trainer
        would optimise a function 0.8% away from the one the engine runs, and
        that error would be systematic rather than noise - the same direction
        for every position, every feature and every game.
        """
        half = self.ft_out // 2
        pair = activation[:, :half] * activation[:, half:] * (self.qa / PAIR_DIVISOR)
        if self.qat:
            # The engine's shift TRUNCATES, so floor here, not round - the same
            # distinction the L1 activation makes, and for the same reason.
            pair = fake_quantize_acts(pair, self.qa)
        return pair

    def _dual_activate(self, pre):
        """clipped activation and its square, concatenated, squares FIRST.

        Order matters and is not arbitrary: the engine writes the squares into
        the low half of its activation buffer and the clipped values into the
        high half, so a swap here would pair every value with the wrong output
        weight and still train to a plausible-looking loss.
        """
        clipped = torch.clamp(pre, 0.0, 1.0)
        squared = clipped * clipped
        if self.qat:
            # The engine computes c*c/QA with INTEGER division, so the square
            # lands back on the QA grid by truncation.
            squared = fake_quantize_acts(squared, self.qa)
        return torch.cat([squared, clipped], dim=-1)

    def _forward_dual(self, stm, opp, piece_count,
                      l1_weight, l1_bias, out_weight, out_bias):
        """Architecture 5 forward pass, mirroring EvaluateArchFive in C#.

        Every bucket is computed and one is gathered at the very end. That is
        wasteful - eight times the head arithmetic - and irrelevant: the head is
        32 and 32 wide against a feature transformer of 22,528 rows. Gathering
        earlier would need per-sample weight matrices and a batched matmul, i.e.
        more memory and more code to express the same function.
        """
        batch = stm.shape[0]
        buckets = self.out_buckets

        l2_weight, l2_bias = self.l2.weight, self.l2.bias
        if self.qat:
            l2_weight = fake_quantize_weights(l2_weight, QB)
            l2_bias = fake_quantize_weights(l2_bias, self.qa * QB)

        x = torch.cat([self._pair(stm), self._pair(opp)], dim=1)

        # ---- first hidden layer ----
        # pre1_raw is kept UNQUANTIZED for the bypass: the engine adds the raw
        # int32 pre-activation to the output, before the division by QB that the
        # fake quantization models. Using the quantized copy there would mirror
        # arithmetic the engine does not perform.
        pre1_raw = F.linear(x, l1_weight, l1_bias).view(batch, buckets, self.l1_out)
        pre1 = fake_quantize_acts(pre1_raw, self.qa) if self.qat else pre1_raw
        act1 = self._dual_activate(pre1)                      # [B, K, 2*l1_out]

        # ---- second hidden layer ----
        w2 = l2_weight.view(buckets, self.l2_out, 2 * self.l1_out)
        b2 = l2_bias.view(buckets, self.l2_out)
        pre2 = torch.einsum("bki,koi->bko", act1, w2) + b2
        if self.qat:
            pre2 = fake_quantize_acts(pre2, self.qa)
        act2 = self._dual_activate(pre2)                      # [B, K, 2*l2_out]

        # ---- output reads BOTH layers ----
        wo = out_weight.view(buckets, 2 * self.l1_out + 2 * self.l2_out)
        scores = torch.einsum("bkn,kn->bk", torch.cat([act1, act2], dim=-1), wo) + out_bias

        # The linear bypass, in the same units as the output by construction.
        scores = scores + pre1_raw[:, :, -2] - pre1_raw[:, :, -1]

        if buckets == 1:
            return scores.squeeze(1)
        bucket = bucket_for_piece_count(piece_count, buckets)
        return scores[torch.arange(batch, device=scores.device), bucket]

    def _indices(self, feats):
        """Maps stored feature indices to EmbeddingBag rows.

        Padding is -1 in the dataset and becomes the zero padding row. Features
        are stored int16 in host RAM to save memory and EmbeddingBag needs Long
        indices, so cast first; .long() is a no-op when the caller already passes
        int64 (validate_nnue builds its batches that way).
        """
        feats = feats.long()
        pad = torch.full_like(feats, self.pad_index)
        real = torch.where(feats < 0, pad, feats)
        if not self.factorized:
            return real

        # Every real feature also fires the king-independent (piece, square)
        # feature it is a copy of. A feature index is base + bucket * PS_NB, so
        # the base is index % PS_NB. Padding stays padding: it contributes
        # nothing and takes no gradient. mode="sum" then adds real + virtual for
        # each active feature, which is exactly what fold_features() bakes in.
        virtual = torch.where(feats < 0, pad, self.virtual_base + feats % PS_NB)
        return torch.cat([real, virtual], dim=1)

    def fold_features(self):
        """Feature transformer as the ENGINE must see it: [INPUT_SIZE, ft_out].

        Folding is exact rather than an approximation. Training computes
        sum over active f of (real[f] + virtual[f % PS_NB]); adding the virtual
        row into each of its 32 real copies produces a single table whose sum
        over the same active features is identical, so the exported net
        evaluates every position to the same value the trained net does.
        """
        real = self.ft.weight[:INPUT_SIZE]
        if not self.factorized:
            return real.detach().clone()
        virtual = self.ft.weight[self.virtual_base:self.virtual_base + PS_NB]
        # repeat tiles the whole block, so row i of the result is virtual[i % PS_NB]
        # - which is the base feature of real row i, by the identity above.
        return (real + virtual.repeat(KING_BUCKET_COUNT, 1)).detach()

    def fold_psqt(self):
        """Psqt head as the ENGINE must see it: [INPUT_SIZE, psqt_buckets].

        Same exactness argument as fold_features: training sums real[f] +
        virtual[f % PS_NB] for every active feature, so adding each virtual row
        into its 32 king-bucket copies reproduces the trained sum exactly.
        """
        real = self.psqt.weight[:INPUT_SIZE]
        if not self.factorized:
            return real.detach().clone()
        virtual = self.psqt.weight[self.virtual_base:self.virtual_base + PS_NB]
        return (real + virtual.repeat(KING_BUCKET_COUNT, 1)).detach()

    def fold_threats(self):
        """Threat transformer as the ENGINE must see it: [60720, ft_out].

        Same exactness argument as fold_features, with one difference that makes
        it harder to get right: a HalfKA feature fires exactly ONE virtual row,
        so folding is `real + virtual[i % PS_NB]` and tiles cleanly. A threat
        feature fires THREE - the (attacker, attacked) pair, the (attacker,
        from) square and the (attacked, to) square - and which three depends on
        the feature, so there is no tiling identity to exploit. The mapping is
        read from the same VIRTUALS table the encoder emits from, which is what
        keeps training and folding using one definition instead of two.

        Rows the deduplication makes unreachable have no virtuals assigned; they
        fold to their real row, which is zero and stays zero.
        """
        real = self.threat_ft.weight[:threats_mod.THREAT_INPUT_SIZE]
        if not self.factorized:
            return real.detach().clone()

        virtual = self.threat_ft.weight[self.threat_virtual_base:self.threat_pad]
        table = torch.from_numpy(threats_mod.VIRTUALS.astype("int64")).to(real.device)

        folded = real.detach().clone()
        for slot in range(table.shape[1]):
            column = table[:, slot]
            live = column >= 0
            # Virtual indices are absolute; the block starts at the base.
            folded[live] += virtual[column[live] - self.threat_virtual_base].detach()
        return folded

    def clip_weights(self):
        """
        Keeps weights inside the ranges the integer inference can represent
        (applied after each optimizer step, like strong engine trainers do).
          ft rows sum over <=32 active features into int16 accumulators;
          l1/out weights are stored as round(w * QB) in int16.
        """
        with torch.no_grad():
            self.ft.weight.clamp_(-1.98, 1.98)          # |w*QA| <= ~505/int16-safe
            self.ft_bias.clamp_(-1.98, 1.98)
            # The int8 architectures need |round(w*QB)| <= 127 for the
            # VPMADDUBSW bound to hold; this clamp is what guarantees the export
            # saturation check passes rather than merely usually passing.
            self.l1.weight.clamp_(-127.0 / QB, 127.0 / QB)
            self.out.weight.clamp_(-127.0 / QB, 127.0 / QB)
            # The second layer is int8 too, and its activations reach QA
            # (a squared clipped activation of 1.0 is still 1.0), so it needs
            # exactly the same bound for the VPMADDUBSW lane to stay exact.
            if self.dual:
                self.l2.weight.clamp_(-127.0 / QB, 127.0 / QB)
