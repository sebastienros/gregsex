using System.Runtime.CompilerServices;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Text;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Text;

public sealed class Pcre2NativeTextTests
{
    [Fact]
    public void ReadersAgreeWithIndependentNativeWindowStartAndCodeUnitObservations()
    {
        var root = Root();
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "test/corpora/pcre2/inputs.json")));
        using var observations = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "test/corpora/pcre2/observations.json")));
        var indexed = observations.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
        int windows = 0, interior = 0, units = 0;
        foreach (var input in inputs.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = input.GetProperty("id").GetString()!;
            var windowCase = id.StartsWith("utfwindow-", StringComparison.Ordinal);
            var interiorCase = id.StartsWith("interior-supplied-", StringComparison.Ordinal);
            // Only \C and \C\C Match have defined primitive consumption.
            var pattern = input.GetProperty("patternUnits").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var codeUnits = id.StartsWith("split-unit-", StringComparison.Ordinal) &&
                input.GetProperty("operation").GetString() == "Match" &&
                (pattern.SequenceEqual([92, 67]) || pattern.SequenceEqual([92, 67, 92, 67]));
            if (!windowCase && !interiorCase && !codeUnits)
            {
                continue;
            }

            var observation = indexed[id];
            Assert.Equal("ExecutedPinnedNativeInterpreter", observation.GetProperty("provenance").GetProperty("kind").GetString());
            Assert.Equal(Pcre2Profile.SourceRevision, observation.GetProperty("provenance").GetProperty("sourceCommit").GetString());
            var events = observation.GetProperty("events").EnumerateArray().ToArray();
            var match = events.First(x => x.GetProperty("event").GetString() == "match");
            var info = events.First(x => x.GetProperty("event").GetString() == "info").GetProperty("values").EnumerateArray().ToArray();
            // INFO_MAXLOOKBEHIND includes word-boundary context; use actual
            // native metadata, never infer it from the future managed parser.
            var maxLookbehind = info.First(x => x.GetProperty("query").GetInt32() == 15).GetProperty("value").GetInt32();
            var width = input.GetProperty("width").GetInt32();
            var start = input.GetProperty("startAt").GetInt32();
            var subject = input.GetProperty("subjectUnits").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, (Pcre2CompileFlags)input.GetProperty("compileFlags").GetUInt32());
            var state = new RunState(default, new());
            Pcre2ValidationResult validation;
            if (width == 8)
            {
                validation = Pcre2ByteTextReader.Validate(subject.Select(x => (byte)x).ToArray(), start, maxLookbehind, profile, 0, 0, ref state);
            }
            else
            {
                validation = Pcre2CharTextReader.Validate(subject.Select(x => (char)x).ToArray(), start, maxLookbehind, profile, 0, 0, ref state);
            }

            var rc = match.GetProperty("returnCode").GetInt32();
            if (rc is <= -3 and >= -26 || rc == -36)
            {
                Assert.False(validation.Valid);
                Assert.Equal(rc, validation.NativeError);
                if (match.GetProperty("errorOffset").ValueKind == JsonValueKind.Number)
                {
                    Assert.Equal(match.GetProperty("errorOffset").GetInt32(), validation.Failure.Offset);
                }
                // BADUTFOFFSET's native StartChar/mark/pairs are undefined.
            }
            else
            {
                Assert.True(validation.Valid, id);
            }

            if (windowCase)
            {
                windows++;
            }

            if (interiorCase)
            {
                interior++;
            }

            if (codeUnits)
            {
                var offset = 0;
                for (var i = 0; i < pattern.Length / 2; i++)
                {
                    var read = width == 8
                        ? Pcre2ByteTextReader.Read(subject.Select(x => (byte)x).ToArray(), offset, subject.Length, true, profile, ref state)
                        : Pcre2CharTextReader.Read(subject.Select(x => (char)x).ToArray(), offset, subject.Length, true, profile, ref state);
                    Assert.Equal(Pcre2ReadStatus.Value, read.Status);
                    offset += read.Width;
                }
                Assert.Equal(0, match.GetProperty("pairs")[0][0].GetInt32());
                Assert.Equal(offset, match.GetProperty("pairs")[0][1].GetInt32());
                units++;
            }
        }
        Assert.Equal(16, windows);
        Assert.Equal(6, interior);
        Assert.Equal(4, units);
    }

    private static string Root([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../../../../../"));
}
