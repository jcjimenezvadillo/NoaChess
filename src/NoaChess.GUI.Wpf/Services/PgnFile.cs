using System.IO;
using System.Windows;
using System.Text;
using Microsoft.Win32;

namespace NoaChess.GUI.Wpf.Services;

// Reading and writing .pgn files: the dialogs, the encoding and the failure
// messages. It knows nothing about chess - the text it moves around is parsed
// and produced by the Core.
public static class PgnFile
{
    private const string Filter = "Portable Game Notation (*.pgn)|*.pgn|All files (*.*)|*.*";

    // Asks for a file and returns its text with the file's name, or (null, "")
    // when the user cancelled. The name is for the picker: "12 games in
    // kasparov.pgn" is worth more than "12 games".
    public static (string? Text, string Name) OpenNamed(Window? owner)
    {
        string? text = Open(owner, out string name);
        return (text, name);
    }

    // Asks for a file and returns its text, or null when the user cancelled.
    // A file that cannot be read reports itself and returns null: the caller
    // has nothing useful to do with the failure that this cannot do here.
    public static string? Open(Window? owner) => Open(owner, out _);

    private static string? Open(Window? owner, out string fileName)
    {
        fileName = "";
        var dialog = new OpenFileDialog
        {
            Title = "Open a game",
            Filter = Filter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(owner) != true)
            return null;

        fileName = Path.GetFileName(dialog.FileName);

        try
        {
            // PGN predates Unicode and most files in the wild are Latin-1, but
            // modern exports are UTF-8. Detecting the byte-order mark and
            // falling back to Latin-1 reads both without mangling player names.
            return ReadWithEncodingFallback(dialog.FileName);
        }
        catch (Exception ex)
        {
            Report(owner, $"That file could not be read.\n\n{ex.Message}", "Open a game");
            return null;
        }
    }

    // Asks where to save and writes the text. Returns the path written, or null.
    public static string? Save(Window? owner, string pgn, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the game",
            Filter = Filter,
            DefaultExt = ".pgn",
            AddExtension = true,
            FileName = Sanitise(suggestedName),
        };

        if (dialog.ShowDialog(owner) != true)
            return null;

        try
        {
            // UTF-8 without a byte-order mark: what every chess database reads
            // today, and what a mark at the front of "[Event" would break.
            File.WriteAllText(dialog.FileName, pgn, new UTF8Encoding(false));
            return dialog.FileName;
        }
        catch (Exception ex)
        {
            Report(owner, $"The game could not be saved.\n\n{ex.Message}", "Save the game");
            return null;
        }
    }

    private static string ReadWithEncodingFallback(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (hasBom)
            return new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3);

        // Strict UTF-8 first: it throws on a byte sequence that is not valid
        // UTF-8, which is exactly how a Latin-1 file announces itself.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // Turns "You vs NoaChess 5.0.0" into something a file system accepts.
    private static string Sanitise(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (char c in name)
            clean.Append(Path.GetInvalidFileNameChars().Contains(c) ? '-' : c);
        return clean.ToString().Trim();
    }

    private static void Report(Window? owner, string message, string title)
    {
        if (owner is not null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
