# NoaChess NNUE architecture id 1 (mirror of the C# inference math in
# NnueInference.cs). Trained in float; quantization happens at export.
#
#   feature transformer: 40960 -> FT_OUT per perspective (shared weights)
#   activation: clipped ReLU to [0, 1]
#   hidden: concat(stm, opp) 2*FT_OUT -> L1_OUT, clipped ReLU
#   output: L1_OUT -> 1  (units: centipawns / OUTPUT_SCALE)

import torch
import torch.nn as nn

INPUT_SIZE = 40960
FT_OUT = 128
L1_OUT = 32
OUTPUT_SCALE = 400.0  # net output * 400 = centipawns

# Quantization scales (must match the C# loader and export_model.py).
QA = 255
QB = 64


class NoaNnue(nn.Module):
    def __init__(self):
        super().__init__()
        # EmbeddingBag with padding trick: sparse sum of feature rows.
        # Index 0..INPUT_SIZE-1 are real features; INPUT_SIZE is a zero
        # padding row so batches can be rectangular.
        self.ft = nn.EmbeddingBag(INPUT_SIZE + 1, FT_OUT, mode="sum", padding_idx=INPUT_SIZE)
        self.ft_bias = nn.Parameter(torch.zeros(FT_OUT))
        self.l1 = nn.Linear(2 * FT_OUT, L1_OUT)
        self.out = nn.Linear(L1_OUT, 1)

        # Small init keeps the quantized ranges healthy from the start.
        nn.init.uniform_(self.ft.weight, -0.05, 0.05)
        with torch.no_grad():
            self.ft.weight[INPUT_SIZE].zero_()

    def forward(self, stm_feats, opp_feats):
        # -1 padding -> the zero row.
        stm_feats = torch.where(stm_feats < 0, torch.full_like(stm_feats, INPUT_SIZE), stm_feats)
        opp_feats = torch.where(opp_feats < 0, torch.full_like(opp_feats, INPUT_SIZE), opp_feats)

        stm = torch.clamp(self.ft(stm_feats) + self.ft_bias, 0.0, 1.0)
        opp = torch.clamp(self.ft(opp_feats) + self.ft_bias, 0.0, 1.0)

        hidden = torch.clamp(self.l1(torch.cat([stm, opp], dim=1)), 0.0, 1.0)
        return self.out(hidden).squeeze(1)

    def clip_weights(self):
        """
        Keeps weights inside the ranges the integer inference can represent
        (applied after each optimizer step, like strong engine trainers do).
          ft rows sum over <=30 active features into int16 accumulators;
          l1/out weights are stored as round(w * QB) in int16.
        """
        with torch.no_grad():
            self.ft.weight.clamp_(-1.98, 1.98)          # |w*QA| <= ~505/int16-safe
            self.ft_bias.clamp_(-1.98, 1.98)
            self.l1.weight.clamp_(-127.0 / QB, 127.0 / QB)
            self.out.weight.clamp_(-127.0 / QB, 127.0 / QB)
