using System.Globalization;
using GregsEx.Text;
using Xunit;

namespace GregsEx.Tests.Text;

public class ValueStringBuilderTests
{
    [Fact]
    public void SupportsSpanFormattingBufferWriterAndDisposal()
    {
        var builder = new ValueStringBuilder();
        builder.Append('a');
        builder.Append('b', 3);
        builder.Append("c");
        builder.Append("de");
        builder.Append("_fg".AsSpan(1));
#if GREGSEX_NETSTANDARD_ASSET
        builder.Append("42");
#else
        builder.Append(42, provider: CultureInfo.InvariantCulture);
#endif
        builder.AppendScalar(0x1F600);
        var span = builder.GetSpan(2);
        span[0] = '!';
        span[1] = '?';
        builder.Advance(2);
        var mem = builder.GetMemory(1);
        mem.Span[0] = '.';
        builder.Advance(1);

        Assert.Equal("abbbcdefg42😀!?.", builder.AsSpan().ToString());
        Assert.Equal("abbb", builder.AsSpan(0, 4).ToString());
        Assert.Equal('a', builder[0]);
        Assert.Equal("abbbcdefg42😀!?.", builder.ToStringAndClear());
        Assert.Equal(0, builder.Length);
        builder.Append(new string('x', 200));
        Assert.Equal(200, builder.ToStringAndDispose().Length);
        Assert.Equal(0, builder.Length);
        Assert.Equal("", builder.ToString());
    }
}
