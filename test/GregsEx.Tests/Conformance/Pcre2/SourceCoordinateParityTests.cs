using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class SourceCoordinateParityTests
{
    [Fact]
    public void C057CharacterConvenienceRetainsNativeAndOriginalLocations()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/inputs.json")));
        using var native = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/observations.json")));
        var input = inputs.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "PCRE-C057");
        var observed = native.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "PCRE-C057").GetProperty("events")[0];
        var budget = new CompileBudget(new());
        Assert.False(Pcre2ExecutorFactory.TryCompile(Chars(Units(input, "sourcePatternUnits")), Profile(input), ref budget, out _, out var diagnostic));
        Assert.Equal(observed.GetProperty("error").GetInt32(), (int)diagnostic.SourceError);
        Assert.Equal(observed.GetProperty("nativeOffset").GetInt32(), diagnostic.NativeOffset);
        Assert.Equal(observed.GetProperty("sourceOffset").GetInt32(), diagnostic.Diagnostic.Offset);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, diagnostic.Diagnostic.LocationUnit);
        Assert.Equal(0, budget.ReservedBytes);
    }
}
