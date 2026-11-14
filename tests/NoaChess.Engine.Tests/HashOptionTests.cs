using NoaChess.UCI.Options;

namespace NoaChess.Engine.Tests;

// The Hash option is declared to the GUI with a maximum, and a client such as python-chess refuses to send a value
// above it, so the declared maximum and the clamp must be the same number.
public class HashOptionTests
{
    [Fact]
    public void HashIsDeclaredAndClampedAtTheSameMaximum()
    {
        var output = new StringWriter();
        new UciOptions().Print(output);
        Assert.Contains($"option name Hash type spin default 64 min 1 max {UciOptions.MaxHashMb}", output.ToString());
        Assert.Equal(8192, UciOptions.MaxHashMb);

        var options = new UciOptions();
        Assert.Equal("Hash", options.Set("Hash", "4096"));
        Assert.Equal(4096, options.Hash);
        options.Set("Hash", "99999");
        Assert.Equal(UciOptions.MaxHashMb, options.Hash);
        options.Set("Hash", "0");
        Assert.Equal(1, options.Hash);
    }
}
