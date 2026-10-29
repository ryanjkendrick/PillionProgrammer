using System.Text;
using System.Text.Json;

namespace Pillion.ModelGateway.Tests.Tools;

/// <summary>The 16 KB cap on tool results (design section 8) and the text a result becomes.</summary>
public sealed class ToolResultsTests
{
    [Fact]
    public void AShortResult_IsSentAsItIs()
    {
        var capped = ToolResults.Cap("hello");

        Assert.Equal("hello", capped.Text);
        Assert.Equal(5, capped.Bytes);
        Assert.False(capped.Truncated);
        Assert.Equal(5, capped.OriginalBytes);
    }

    [Fact]
    public void AResultOfExactlyTheCap_IsNotTruncated()
    {
        var text = new string('x', ToolResults.MaxBytes);

        var capped = ToolResults.Cap(text);

        Assert.False(capped.Truncated);
        Assert.Equal(ToolResults.MaxBytes, capped.Bytes);
    }

    [Fact]
    public void ALongResult_IsCutToTheCap_WithAMarker()
    {
        var text = new string('x', 100_000);

        var capped = ToolResults.Cap(text);

        Assert.True(capped.Truncated);
        Assert.True(capped.Bytes <= ToolResults.MaxBytes, $"{capped.Bytes} bytes");
        Assert.Equal(capped.Bytes, Encoding.UTF8.GetByteCount(capped.Text));
        Assert.Equal(100_000, capped.OriginalBytes);
        Assert.EndsWith("of 100000 bytes shown; ask for the rest with read_more]", capped.Text, StringComparison.Ordinal);
        Assert.Contains("[truncated by Pillion:", capped.Text, StringComparison.Ordinal);

        // The marker reports exactly what was kept.
        var head = capped.Text[..capped.Text.IndexOf("\n[truncated", StringComparison.Ordinal)];
        Assert.Contains($"{Encoding.UTF8.GetByteCount(head)} of 100000", capped.Text, StringComparison.Ordinal);
        Assert.True(ToolResults.MaxBytes - capped.Bytes < 16, "the cap is used almost to the byte");
    }

    [Fact]
    public void TheCut_NeverSplitsAMultiByteCharacterOrASurrogatePair()
    {
        // Three bytes per character, then four-byte emoji: the cut lands between characters, never inside one.
        var text = string.Concat(Enumerable.Repeat("€", 6000)) + string.Concat(Enumerable.Repeat("\U0001F600", 2000));

        var capped = ToolResults.Cap(text);

        Assert.True(capped.Truncated);
        Assert.True(capped.Bytes <= ToolResults.MaxBytes);
        var head = capped.Text[..capped.Text.IndexOf("\n[truncated", StringComparison.Ordinal)];
        Assert.False(char.IsHighSurrogate(head[^1]));
        Assert.Equal(capped.Bytes, Encoding.UTF8.GetByteCount(capped.Text));
        Assert.DoesNotContain('�', capped.Text);
    }

    [Fact]
    public void Text_UsesStringsAsTheyAre_AndSerialisesEverythingElse()
    {
        Assert.Equal(string.Empty, ToolResults.Text(null));
        Assert.Equal("plain", ToolResults.Text("plain"));
        Assert.Equal("{\"a\":1}", ToolResults.Text(new { a = 1 }));
        using var document = JsonDocument.Parse("{\"b\":[1,2]}");
        Assert.Equal("{\"b\":[1,2]}", ToolResults.Text(document.RootElement.Clone()));
        using var text = JsonDocument.Parse("\"quoted\"");
        Assert.Equal("quoted", ToolResults.Text(text.RootElement.Clone()));
    }
}
