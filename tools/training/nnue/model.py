# NoaChess NNUE architecture id 1 (mirror of the C# inference math in
# NnueInference.cs). Trained in float; quantization happens at export.
#
#   feature transformer: 22528 (HalfKAv2_hm) -> FT_OUT per perspective (shared)
#   activation: clipped ReLU to [0, 1]
#   hidden: concat(stm, opp) 2*FT_OUT -> L1_OUT, clipped ReLU
#   output: L1_OUT -> 1  (units: centipawns / OUTPUT_SCALE)

import torch
import torch.nn as nn

from dataset import INPUT_SIZE  # HalfKAv2_hm feature count (22,528); single source.

FT_OUT = 128
L1_OUT = 32
OUTPUT_SCALE = 400.0  # net output * 400 = centipawns

# Quantization scales (must match the C# loader and export_model.py).
QA = 255
QB = 64


class NoaNnue(nn.Module):
    # ft_out / l1_out default to the module constants but can be widened to
    # sweep capacity (256, 512, ...). The C# runtime reads both dimensions from
    # the model header, so a wider net needs no engine change — only a retrain.
    def __init__(self, ft_out=FT_OUT, l1_out=L1_OUT):
        super().__init__()
        self.ft_out = ft_out
        self.l1_out = l1_out
        # EmbeddingBag with padding trick: sparse sum of feature rows.
        # Index 0..INPUT_SIZE-1 are real features; INPUT_SIZE is a zero
        # padding row so batches can be rectangular.
        self.ft = nn.EmbeddingBag(INPUT_SIZE + 1, ft_out, mode="sum", padding_idx=INPUT_SIZE)
        self.ft_bias = nn.Parameter(torch.zeros(ft_out))
        self.l1 = nn.Linear(2 * ft_out, l1_out)
        self.out = nn.Linear(l1_out, 1)

        # Small init keeps the quantized ranges healthy from the start.
        nn.init.uniform_(self.ft.weight, -0.05, 0.05)
        with torch.no_grad():
            self.ft.weight[INPUT_SIZE].zero_()

    def forward(self, stm_feats, opp_feats):
        # -1 padding -> the zero row. Features are stored int16 in host RAM to
        # save memory; INPUT_SIZE (22528) fits int16, and EmbeddingBag needs
        # Long indices, so cast after remapping. .long() is a no-op when the
        # caller already passes int64 (e.g. validate_nnue's on-the-fly batches).
        stm_feats = torch.where(stm_feats < 0, torch.full_like(stm_feats, INPUT_SIZE), stm_feats).long()
        opp_feats = torch.where(opp_feats < 0, torch.full_like(opp_feats, INPUT_SIZE), opp_feats).long()

        stm = torch.clamp(self.ft(stm_feats) + self.ft_bias, 0.0, 1.0)
        opp = torch.clamp(self.ft(opp_feats) + self.ft_bias, 0.0, 1.0)

        hidden = torch.clamp(self.l1(torch.cat([stm, opp], dim=1)), 0.0, 1.0)
        return self.out(hidden).squeeze(1)

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
            self.l1.weight.clamp_(-127.0 / QB, 127.0 / QB)
            self.out.weight.clamp_(-127.0 / QB, 127.0 / QB)
