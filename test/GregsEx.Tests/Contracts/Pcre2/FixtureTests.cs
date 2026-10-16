using System.Runtime.CompilerServices;
using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Contracts.Pcre2;

public class FixtureTests
{
    [Fact]
    public void SourceCorpusPinsEveryInventoryAndPreservesNativeEndpointsAndUnits()
    {
        var json = File.ReadAllText(Path.Combine(Directory(), "ContractVectors.json"));
        var corpus = Pcre2FixtureCorpus.Read(json);
        Assert.Equal(67, corpus.Cases.Length);
        for (var n = 1; n <= 24; n++)
        {
            Assert.Contains(corpus.Cases, c => c.Requirements.Contains($"PC-{n:000}"));
        }

        for (var n = 1; n <= 12; n++)
        {
            Assert.Contains(corpus.Cases, c => c.Requirements.Contains($"PH-{n:000}"));
        }

        Assert.Contains(corpus.Cases, c => c.Width == 8 && c.CompileFlags == 0x80000 && c.SubjectUnits.SequenceEqual([240, 159, 152, 128]));
        Assert.Contains(corpus.Cases, c => c.SubjectUnits.Contains(0xD800));
        Assert.Contains(corpus.Cases, c => c.SubjectUnits.Contains(0));
        Assert.Contains(corpus.Cases, c => c.Expected.Pairs.Any(p => p.Start > p.End));
        Assert.Contains(corpus.Cases, c => c.Expected.Outcome == "Partial" && c.Expected.NativeReturn == -2);
        Assert.Contains(corpus.Cases, c => c.Expected.Outcome == "Match" && c.Expected.NativeReturn == 0);
        Assert.Contains(corpus.Cases, c => c.Expected.NativeReturn == -1000);
        Assert.Contains(corpus.Cases, c => c.Expected.NativeOffset == 5 && c.Expected.SourceOffset == 3);
        Assert.Contains(corpus.Cases, c => c.Expected.SubstituteCallouts.Any(v => v.ReturnCode == -1));
        Assert.Contains(corpus.Cases, c => c.Expected.CaseCallouts.Any(v => v.Mode == 2 && v.Overlap));
        Assert.Contains(corpus.Cases, c => c.Expected.NameNumbers.SequenceEqual([1, 2]));
        Assert.Equal(corpus.Cases[0].PatternUnits, Pcre2FixtureCorpus.Read(JsonSerializer.Serialize(corpus, Pcre2FixtureCorpus.JsonOptions)).Cases[0].PatternUnits);
        Assert.Throws<JsonException>(() => Pcre2FixtureCorpus.Read(json.Replace("\"startAt\"", "\"unknownStart\"")));
        Assert.Throws<InvalidDataException>(() => Pcre2FixtureCorpus.Read(json.Replace("pcre2-fixtures-1", "pcre2-fixtures-2")));
        var malformed = corpus with { Cases = [corpus.Cases[0] with { SubjectUnits = [256] }] };
        Assert.Throws<InvalidDataException>(() => Pcre2FixtureCorpus.Read(JsonSerializer.Serialize(malformed, Pcre2FixtureCorpus.JsonOptions)));
    }
    [Fact]
    public void NativeMajorAndParticularPropertyNamespacesCannotBeConfused()
    {
        Assert.Equal(0, (int)Pcre2MajorCategory.C);
        Assert.Equal(0, (int)Pcre2ParticularCategory.Cc);
        Assert.True(Pcre2DataAbi.IsValidProperty(new(Pcre2PropertyKind.ParticularCategory, 9)));
        Assert.False(Pcre2DataAbi.IsValidProperty(new(Pcre2PropertyKind.MajorCategory, 9)));
        Assert.Equal(6, Pcre2DataAbi.ScriptWords);
        Assert.Equal(2, Pcre2DataAbi.BooleanWords);
        Assert.False(Pcre2DataAbi.IsValidProperty(new(Pcre2PropertyKind.Script, (int)Pcre2Script.Script_Count)));
        Assert.False(Pcre2DataAbi.IsValidProperty(new(Pcre2PropertyKind.Binary, (int)Pcre2BooleanProperty.Bprop_Count)));
    }
    [Fact]
    public void SearchStartDoesNotFollowCandidateBumpalongOrResetStart()
    {
        var machine = new Pcre2VmState(Pcre2ExecutionCapacity.Default, new(), 8, startAt: 2)
        {
            CandidateStart = 5,
            Position = 6,
            ReportStart = 7
        };
        Assert.Equal(2, machine.SearchStart);
    }
    private static string Directory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
