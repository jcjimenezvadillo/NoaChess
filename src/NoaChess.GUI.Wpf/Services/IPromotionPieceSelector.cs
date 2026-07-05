using NoaChess.Core;

namespace NoaChess.GUI.Wpf.Services;

// Service that asks the user which piece to promote a pawn to.
// It is abstracted behind an interface so the BoardViewModel does not depend
// on WPF dialogs and can be tested without a graphical interface.
public interface IPromotionPieceSelector
{
    // Returns the chosen piece (queen, rook, bishop or knight).
    PieceType SelectPromotionPiece(Color color);
}
