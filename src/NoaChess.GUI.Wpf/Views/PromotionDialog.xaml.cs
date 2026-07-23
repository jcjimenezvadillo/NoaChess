using System.Windows;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Services;

namespace NoaChess.GUI.Wpf.Views;

// Modal dialog for choosing the promotion piece. A WPF window can only be
// shown once, which is why the reusable service is the nested Service class,
// which creates a fresh dialog on every promotion.
public partial class PromotionDialog : Window
{
    // Chosen piece; queen by default if the user closes the dialog.
    public PieceType SelectedPiece { get; private set; } = PieceType.Queen;

    public PromotionDialog(Color color)
    {
        InitializeComponent();

        // Vector piece images matching the color of the promoting pawn.
        QueenImage.Source = PieceImageProvider.Get(color, PieceType.Queen);
        RookImage.Source = PieceImageProvider.Get(color, PieceType.Rook);
        BishopImage.Source = PieceImageProvider.Get(color, PieceType.Bishop);
        KnightImage.Source = PieceImageProvider.Get(color, PieceType.Knight);
    }

    private void Choose(PieceType piece)
    {
        SelectedPiece = piece;
        DialogResult = true;
    }

    private void OnQueenClicked(object sender, RoutedEventArgs e) => Choose(PieceType.Queen);
    private void OnRookClicked(object sender, RoutedEventArgs e) => Choose(PieceType.Rook);
    private void OnBishopClicked(object sender, RoutedEventArgs e) => Choose(PieceType.Bishop);
    private void OnKnightClicked(object sender, RoutedEventArgs e) => Choose(PieceType.Knight);

    // WPF implementation of the promotion selector used by the ViewModel.
    public sealed class Service : IPromotionPieceSelector
    {
        public PieceType SelectPromotionPiece(Color color)
        {
            var dialog = new PromotionDialog(color) { Owner = Application.Current.MainWindow };
            dialog.ShowDialog();
            return dialog.SelectedPiece;
        }
    }
}
