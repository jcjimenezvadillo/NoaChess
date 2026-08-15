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
                 factorized=FACTORIZED, qat=False, qa=QA, threats=False):
        super().__init__()
        self.ft_out = ft_out
        self.l1_out = l1_out
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
        self.l1 = nn.Linear(2 * ft_out, self.out_buckets * l1_out)
        self.out = nn.Linear(l1_out, self.out_buckets)

        # Small init keeps the quantized ranges healthy from the start.
        nn.init.uniform_(self.ft.weight, -0.05, 0.05)
        with torch.no_grad():
            self.ft.weight[self.pad_index].zero_()

    def forward(self, stm_feats, opp_feats, stm_threats=None, opp_threats=None):
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

        def transform(feats, threat_feats):
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
                acc = acc + F.embedding_bag(threat_feats, threat_weight, mode="sum",
                                            padding_idx=self.threat_pad)
            return torch.clamp(acc, 0.0, 1.0)

        stm = transform(stm_feats, stm_threats)
        opp = transform(opp_feats, opp_threats)

        hidden_pre = F.linear(torch.cat([stm, opp], dim=1), l1_weight, l1_bias)
        if self.qat:
            # The engine computes clip((l1_b + l1_w @ x) // QB, 0, QA): integer
            # division first, then the clamp. Same order here.
            hidden_pre = fake_quantize_acts(hidden_pre, self.qa)
        hidden_all = torch.clamp(hidden_pre, 0.0, 1.0)
        if self.out_buckets == 1:
            return F.linear(hidden_all, out_weight, out_bias).squeeze(1)

        # Select this sample's bucket. Every bucket is computed and one is
        # gathered, which is wasteful in training and irrelevant in cost (l1_out
        # is 32), while play time evaluates the selected bucket only.
        batch = hidden_all.shape[0]
        bucket = bucket_for_piece_count(piece_count, self.out_buckets)
        hidden = hidden_all.view(batch, self.out_buckets, self.l1_out)[
            torch.arange(batch, device=hidden_all.device), bucket]
        weight = out_weight[bucket]             # [batch, l1_out]
        bias = out_bias[bucket]                 # [batch]
        return (hidden * weight).sum(dim=1) + bias

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
