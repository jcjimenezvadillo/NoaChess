# Builds a small RANDOM checkpoint for a given architecture, so the C# engine
# and the Python trainer can be compared on the exact same weights without
# waiting days for a real training run.
#
# WHY THIS EXISTS. A quantization contract between two independent
# implementations fails silently: the engine simply evaluates a slightly
# different network forever, and the only symptom is a net that measures worse
# than it should. verify_export.py can reproduce the engine's arithmetic, but it
# needs a FILE to read, and before a new architecture has ever been trained
# there is none. Random weights answer the contract question just as well as
# trained ones - the arithmetic does not care whether the numbers mean anything.
#
# It is also what prices a new architecture in NPS before committing to a
# training run, which is how the threat features were costed days before their
# net existed.
#
# Usage:
#   python make_test_net.py --out C:/NoaData/t_arch5.pt --dual --buckets 8
#   python export_model.py --checkpoint C:/NoaData/t_arch5.pt \
#       --out C:/NoaData/t_arch5.noannue --arch 5

import argparse

import torch

from model import NoaNnue, FT_OUT, L1_OUT, L2_OUT


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--ft-out", type=int, default=FT_OUT)
    parser.add_argument("--l1-out", type=int, default=L1_OUT)
    parser.add_argument("--l2-out", type=int, default=L2_OUT)
    parser.add_argument("--buckets", type=int, default=8)
    parser.add_argument("--dual", action="store_true")
    parser.add_argument("--threats", action="store_true")
    parser.add_argument("--factorized", action="store_true")
    parser.add_argument("--seed", type=int, default=1)
    # The transformer scale has to stay small or the export's accumulator
    # headroom check fails: up to MAX_ACTIVE rows sum into one int16 lane, and
    # a random table has no reason to cancel the way a trained one does.
    parser.add_argument("--ft-scale", type=float, default=0.01)
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    qa = 127 if args.dual else 255
    model = NoaNnue(args.ft_out, args.l1_out, args.buckets, args.factorized,
                    qa=qa, threats=args.threats,
                    dual=args.dual, l2_out=args.l2_out)

    with torch.no_grad():
        for name, tensor in model.named_parameters():
            if "ft" in name and tensor.dim() == 2:
                tensor.uniform_(-args.ft_scale, args.ft_scale)
            else:
                tensor.uniform_(-0.5, 0.5)
        # The padding rows must stay zero or they contribute to every position.
        model.ft.weight[model.pad_index].zero_()
        if args.threats:
            model.threat_ft.weight[model.threat_pad].zero_()
    model.clip_weights()

    torch.save({
        "model": model.state_dict(),
        "args": {
            "ft_out": args.ft_out,
            "l1_out": args.l1_out,
            "l2_out": args.l2_out,
            "out_buckets": args.buckets,
            "factorized": args.factorized,
            "threats": args.threats,
            "dual": args.dual,
        },
    }, args.out)
    print(f"wrote {args.out}: ft_out={args.ft_out} l1_out={args.l1_out} "
          f"l2_out={args.l2_out if args.dual else 0} buckets={args.buckets} "
          f"dual={args.dual} threats={args.threats} qa={qa}")


if __name__ == "__main__":
    main()
