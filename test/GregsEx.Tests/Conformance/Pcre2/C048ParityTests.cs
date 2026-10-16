using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class C048ParityTests
{
    [Fact]
    public void OriginalExplicitOneEffectiveTwoAndNullAutomaticRemainDistinct()
    {
        // Original contract descriptor bytes remain unchanged. This reads actual
        // independently observed native reductions, not the original advisory success expectation.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/oracle/C048-reduction.json")));
        foreach (var row in doc.RootElement.GetProperty("invocations").EnumerateArray())
        {
            var width = row.GetProperty("width").GetInt32();
            int? capacity = row.GetProperty("matchData").GetString() == "NULL-auto-full" ? null : row.GetProperty("pairCapacity").GetInt32();
            var budget = new CompileBudget(new());
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width);
            var ok = width == 8 ? Pcre2ExecutorFactory.TryCompile("(a)"u8, profile, ref budget, out var executor, out var diagnostic)
                : Pcre2ExecutorFactory.TryCompile("(a)".AsSpan(), profile, ref budget, out executor, out diagnostic);
            Assert.True(ok, diagnostic.ToString());
            using var workspace = new Pcre2PreparedWorkspace(executor!, new(128, 512, 64, 512));
            Pcre2SubstituteResult result;
            int[] output;
            if (width == 8)
            { var bytes = new byte[128]; result = workspace.Substitute("a"u8, 0, "$1$1"u8, bytes, pairCapacity: capacity); output = result.NativeReturnCode >= 0 ? [.. bytes[..result.OutputUnits].Select(x => (int)x)] : []; }
            else
            { var chars = new char[128]; result = workspace.Substitute("a", 0, "$1$1", chars, pairCapacity: capacity); output = result.NativeReturnCode >= 0 ? [.. chars[..result.OutputUnits].Select(x => (int)x)] : []; }
            Assert.Equal(row.GetProperty("returnCode").GetInt32(), result.NativeReturnCode);
            Assert.Equal(row.GetProperty("reportedLength").GetInt32(), result.OutputUnits);
            Assert.Equal(Units(row, "outputUnits"), output);
            Assert.True(workspace.IsIdle);
        }
    }
}
