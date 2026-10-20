using TunasDM.Core.Management;

namespace TunasDM.Core.Tests;

public sealed class BatchPatternTests
{
    [Fact]
    public void Numbers_WithAndWithoutPadding()
    {
        Assert.Equal(["https://x.de/f1.jpg", "https://x.de/f2.jpg", "https://x.de/f3.jpg"], BatchPattern.Expand("https://x.de/f[1-3].jpg"));
        Assert.Equal(["a08", "a09", "a10"], BatchPattern.Expand("a[08-10]"));
        Assert.Equal(["p8", "p9", "p10"], BatchPattern.Expand("p[8-10]"));
        Assert.Equal(["s001", "s002"], BatchPattern.Expand("s[001-2]"));
    }

    [Fact]
    public void Steps_Descending_Letters()
    {
        Assert.Equal(["0", "5", "10"], BatchPattern.Expand("[0-10:5]"));
        Assert.Equal(["3", "2", "1"], BatchPattern.Expand("[3-1]"));
        Assert.Equal(["a", "b", "c"], BatchPattern.Expand("[a-c]"));
        Assert.Equal(["X", "Y", "Z"], BatchPattern.Expand("[X-Z]"));
    }

    [Fact]
    public void SeveralRanges_AreCombined()
    {
        var links = BatchPattern.Expand("https://x.de/s[1-2]e[01-03].mp4");
        Assert.Equal(6, links.Count);
        Assert.Equal("https://x.de/s1e01.mp4", links[0]);
        Assert.Equal("https://x.de/s2e03.mp4", links[^1]);
        Assert.Equal(6, BatchPattern.Count("https://x.de/s[1-2]e[01-03].mp4"));
    }

    [Fact]
    public void NoPattern_Invalid_TooMany()
    {
        Assert.False(BatchPattern.HasPattern("https://[::1]/datei.zip"));
        Assert.Equal(["https://x.de/a.zip"], BatchPattern.Expand("https://x.de/a.zip"));
        Assert.Throws<FormatException>(() => BatchPattern.Expand("[a-Z]"));
        Assert.Throws<FormatException>(() => BatchPattern.Expand("[1-10:0]"));
        Assert.Equal(0, BatchPattern.Count("[a-Z]"));
        Assert.Throws<FormatException>(() => BatchPattern.Expand("[1-200][1-100]"));
    }
}
