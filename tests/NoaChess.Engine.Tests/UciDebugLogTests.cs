using NoaChess.UCI;

namespace NoaChess.Engine.Tests;

public sealed class UciDebugLogTests
{
    [Fact]
    public void EmptyOptionClosesAndUnlocksAnActiveLog()
    {
        string path = Path.Combine(Path.GetTempPath(), $"noachess-{Guid.NewGuid():N}.log");
        string commands = $"setoption name Debug Log File value {path}\n"
                        + "setoption name Debug Log File value <empty>\n"
                        + "isready\nquit\n";

        try
        {
            new UciLoop(new StringReader(commands), TextWriter.Null).Run();

            using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidSwitchKeepsTheActiveLogUsable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"noachess-{Guid.NewGuid():N}.log");
        var output = new StringWriter();
        string commands = $"setoption name Debug Log File value {path}\n"
                        + "setoption name Debug Log File value ?:\\invalid.log\n"
                        + "isready\n"
                        + "setoption name Debug Log File value <empty>\n"
                        + "quit\n";

        try
        {
            new UciLoop(new StringReader(commands), output).Run();

            string protocol = output.ToString();
            string log = File.ReadAllText(path);
            Assert.Contains("info string debug log rejected:", protocol);
            Assert.Contains("info string debug log rejected:", log);
            Assert.Contains("readyok", log);
            using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QuitIsDistinguishedFromInputEof()
    {
        string quitPath = Path.Combine(Path.GetTempPath(), $"noachess-quit-{Guid.NewGuid():N}.log");
        string eofPath = Path.Combine(Path.GetTempPath(), $"noachess-eof-{Guid.NewGuid():N}.log");

        try
        {
            new UciLoop(
                new StringReader($"setoption name Debug Log File value {quitPath}\nquit\n"),
                TextWriter.Null).Run();
            new UciLoop(
                new StringReader($"setoption name Debug Log File value {eofPath}\n"),
                TextWriter.Null).Run();

            string quitLog = File.ReadAllText(quitPath);
            string eofLog = File.ReadAllText(eofPath);
            Assert.Contains("quit received — read loop ends", quitLog);
            Assert.DoesNotContain("stdin EOF — read loop ends", quitLog);
            Assert.Contains("stdin EOF — read loop ends", eofLog);
            Assert.DoesNotContain("quit received — read loop ends", eofLog);
        }
        finally
        {
            File.Delete(quitPath);
            File.Delete(eofPath);
        }
    }
}
