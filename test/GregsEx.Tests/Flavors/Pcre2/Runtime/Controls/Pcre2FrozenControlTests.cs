using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2FrozenControlTests
{
    private static string Corpus(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = "test/corpora/pcre2/" + file;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative)))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, relative);
    }
    private static bool Selected(JsonElement input)
    {
        var id = input.GetProperty("id").GetString()!;
        if (id.StartsWith("binary-mark-", StringComparison.Ordinal) || id.StartsWith("binary-string-", StringComparison.Ordinal))
        {
            return true;
        }

        if (!id.StartsWith("PCRE-C", StringComparison.Ordinal) || !int.TryParse(id.AsSpan(6, 3), out var number))
        {
            return false;
        }

        return number is 30 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 41 or 42 or 43 or 46 or 51 or 52 or 56;
    }
    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Corpus("inputs.json")));
        foreach (var input in inputs.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (Selected(input))
            {
                yield return new TheoryDataRow<string>(input.GetProperty("id").GetString()!);
            }
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void ActualCompiledPolicyMatchesFrozenNativeControlObservation(string id)
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Corpus("inputs.json")));
        using var observations = JsonDocument.Parse(File.ReadAllText(Corpus("observations.json")));
        var input = inputs.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == id);
        var events = observations.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == id)
            .GetProperty("events").EnumerateArray().ToArray();
        var optimization = input.GetProperty("optimization").EnumerateArray().Select(v => v.GetBoolean()).ToArray();
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(),
            (Pcre2CompileFlags)input.GetProperty("compileFlags").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extraFlags").GetUInt32(),
            (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(),
            optimization: new(optimization[0], optimization[1], optimization[2]));
        var pattern = input.GetProperty("patternUnits").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var budget = new CompileBudget(new());
        var compiler = new Pcre2Compiler();
        var compiled = profile.Width == Pcre2CodeUnitWidth.Eight ?
            compiler.TryCompile(pattern.Select(v => (byte)v).ToArray(), profile, Pcre2Data.Instance, ref budget, out var program, out var diagnostic) :
            compiler.TryCompile(new string([.. pattern.Select(v => (char)v)]), profile, Pcre2Data.Instance, ref budget, out program, out diagnostic);
        Assert.True(compiled, diagnostic.ToString());
        Assert.True(Pcre2ControlPlan.TryCreate(program!, ref budget, out var plan, out diagnostic), diagnostic.ToString());
        var fixture = new ControlFixture(plan!);
        var capacity = input.GetProperty("pairCapacity").GetInt32();
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 1234567, 1234567), capacity).ToArray();
        if (input.GetProperty("initialPairs").GetArrayLength() > 0)
        {
            var initial = input.GetProperty("initialPairs").EnumerateArray().ToArray();
            for (var i = 0; i < Math.Min(initial.Length, pairs.Length); i++)
            {
                pairs[i] = new(initial[i].GetProperty("participated").GetBoolean(),
                    initial[i].GetProperty("start").GetInt32(), initial[i].GetProperty("end").GetInt32());
            }
        }
        var initialPairs = pairs.ToArray();
        var subject = input.GetProperty("subjectUnits").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var expectedCalls = events.Where(row => row.GetProperty("event").GetString() == "callout").ToArray();
        var returns = input.GetProperty("callbackReturns").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var called = 0;
        int Inspect(Pcre2CalloutInfo info, int[] mark, int[] text, Pcre2OffsetPair[] captures)
        {
            Assert.True(called < expectedCalls.Length, "Unexpected callout occurrence.");
            var expected = expectedCalls[called];
            Assert.Equal(expected.GetProperty("number").GetInt32(), info.Number);
            Assert.Equal(expected.GetProperty("isString").GetBoolean(), info.IsString);
            Assert.Equal(expected.GetProperty("subjectLength").GetInt32(), info.SubjectLength);
            Assert.Equal(expected.GetProperty("captureTop").GetInt32(), info.CaptureTop);
            Assert.Equal(expected.GetProperty("captureLast").GetInt32(), info.CaptureLast);
            Assert.Equal(expected.GetProperty("startMatch").GetInt32(), info.StartMatch);
            Assert.Equal(expected.GetProperty("currentPosition").GetInt32(), info.CurrentPosition);
            Assert.Equal(expected.GetProperty("patternPosition").GetInt32(), info.PatternPosition);
            Assert.Equal(expected.GetProperty("nextItemLength").GetInt32(), info.NextItemLength);
            Assert.Equal(expected.GetProperty("stringOffset").GetInt32(), info.StringOffset);
            Assert.Equal(expected.GetProperty("flags").GetUInt32(), (uint)info.Flags);
            if (expected.GetProperty("markUnits").ValueKind == JsonValueKind.Null)
            {
                Assert.False(info.HasMark);
            }
            else
            { Assert.True(info.HasMark); Assert.Equal(expected.GetProperty("markUnits").EnumerateArray().Select(v => v.GetInt32()), mark); }
            Assert.Equal(expected.GetProperty("stringUnits").EnumerateArray().Select(v => v.GetInt32()), text);
            var index = 0;
            foreach (var pair in expected.GetProperty("captures").EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Null || pair[0].ValueKind == JsonValueKind.Null)
                {
                    Assert.False(captures[index].Participated);
                }
                else
                {
                    Assert.Equal(new(true, pair[0].GetInt32(), pair[1].GetInt32()), captures[index]);
                }

                index++;
            }
            var result = called < returns.Length ? returns[called] : 0;
            called++;
            return result;
        }
        var options = new Pcre2MatchOptions { Flags = (Pcre2MatchFlags)input.GetProperty("matchFlags").GetUInt32() };
        Pcre2MatchResult actual;
        if (profile.Width == Pcre2CodeUnitWidth.Eight)
        {
            var callbacks = input.GetProperty("callbackEnabled").GetBoolean() ? new Pcre2ByteCallbacks((in c, _) =>
                Inspect(c.Info, [.. c.Mark.ToArray().Select(v => (int)v)], [.. c.CalloutString.ToArray().Select(v => (int)v)], c.Captures.ToArray())) : null;
            actual = fixture.Match([.. subject.Select(v => (byte)v)], pairs, options, callbacks, start: input.GetProperty("startAt").GetInt32());
        }
        else
        {
            var callbacks = input.GetProperty("callbackEnabled").GetBoolean() ? new Pcre2CharCallbacks((in c, _) =>
                Inspect(c.Info, [.. c.Mark.ToArray().Select(v => (int)v)], [.. c.CalloutString.ToArray().Select(v => (int)v)], c.Captures.ToArray())) : null;
            actual = fixture.Match(new string([.. subject.Select(v => (char)v)]), pairs, options, callbacks, start: input.GetProperty("startAt").GetInt32());
        }
        Assert.Equal(expectedCalls.Length, called);
        var native = events.Single(row => row.GetProperty("event").GetString() == "match");
        Assert.Equal(native.GetProperty("returnCode").GetInt32(), actual.NativeReturnCode);
        if (native.GetProperty("startChar").ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(native.GetProperty("startChar").GetInt32(), actual.StartChar);
        }

        if (native.GetProperty("errorOffset").ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(native.GetProperty("errorOffset").GetInt32(), actual.Failure.Offset);
        }

        var group = 0;
        foreach (var pair in native.GetProperty("pairs").EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Null || pair[0].ValueKind == JsonValueKind.Null)
            {
                Assert.False(pairs[group].Participated);
            }
            else
            {
                Assert.Equal(new(true, pair[0].GetInt32(), pair[1].GetInt32()), pairs[group]);
            }

            group++;
        }
        if (actual.NativeReturnCode < 0 && actual.NativeReturnCode != (int)Pcre2ErrorCode.Partial)
        {
            Assert.Equal(initialPairs, pairs);
        }

        if (native.GetProperty("markDefined").GetBoolean())
        {
            if (native.GetProperty("markUnits").ValueKind == JsonValueKind.Null)
            {
                Assert.Equal(-1, actual.MarkIndex);
            }
            else
            {
                Assert.Equal(native.GetProperty("markUnits").EnumerateArray().Select(v => v.GetInt32()),
                program!.Units.Slice(program.Marks[actual.MarkIndex].Offset, program.Marks[actual.MarkIndex].Count).ToArray());
            }
        }
        Assert.True(fixture.Released);
    }
}
