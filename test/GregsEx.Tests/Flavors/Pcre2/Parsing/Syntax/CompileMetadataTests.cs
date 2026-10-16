using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Syntax;

public sealed class CompileMetadataTests
{
    [Fact]
    public void PinnedNativeCompileInfoMatchesLexicalProvenanceInFourDomains()
    {
        var report = Path.Combine(Root(), "test/corpora/pcre2/compile-metadata");
        using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "native-inputs.json")));
        var observed = new Dictionary<string, JsonElement>();
        foreach (var width in new[] { 8, 16 })
        {
            foreach (var line in File.ReadLines(Path.Combine(report, $"native-{width}.jsonl")))
            { using var row = JsonDocument.Parse(line); observed.Add(row.RootElement.GetProperty("id").GetString()!, row.RootElement.Clone()); }
        }

        int accepted = 0, rejected = 0;
        var failures = new List<string>();
        foreach (var row in input.RootElement.EnumerateArray())
        {
            string id = row.GetProperty("id").GetString()!, pattern = row.GetProperty("pattern").GetString()!;
            var native = observed[id];
            var width = (Pcre2CodeUnitWidth)row.GetProperty("width").GetInt32();
            var tables = Pcre2CharacterTables.Default.Bytes.ToArray();
            if (row.GetProperty("imported").GetBoolean())
            { tables[256 + 'a'] = 10; tables[256 + 10] = (byte)'a'; }
            var profile = new Pcre2Profile(width, (Pcre2CompileFlags)row.GetProperty("flags").GetUInt32(),
                (Pcre2ExtraFlags)row.GetProperty("extra").GetUInt32(), characterTables: new(tables));
            var units = row.GetProperty("units").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var budget = new CompileBudget(new());
            var ok = width == Pcre2CodeUnitWidth.Eight ? new Pcre2SyntaxParser().TryParse(units.Select(x => (byte)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var diagnostic) :
                new Pcre2SyntaxParser().TryParse(units.Select(x => (char)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out syntax, out diagnostic);
            if (!native.GetProperty("accepted").GetBoolean())
            {
                // Native rejection has no INFO value; check source admission
                // and native offsets independently of compile metadata.
                rejected++;
                if (ok || (int)diagnostic.SourceError != native.GetProperty("error").GetInt32() || diagnostic.NativeOffset != native.GetProperty("offset").GetInt32())
                {
                    failures.Add($"{id}: native rejection differs: {diagnostic}");
                }

                Assert.Equal(0, budget.ReservedBytes);
                continue;
            }
            accepted++;
            if (!ok)
            { failures.Add($"{id}: unexpected rejection {diagnostic}"); continue; }
            var expected = native.GetProperty("hasCrOrLf").GetInt32() != 0;
            if (syntax!.HasExplicitCrOrLf != expected)
            {
                failures.Add($"{id}: expected {expected}, got {syntax.HasExplicitCrOrLf}");
            }

            Assert.Equal(syntax.RetainedBytes, budget.ReservedBytes);
            Assert.Null(diagnostic.Diagnostic.Message);
            // The character convenience entry point must retain native INFO while
            // mapping UTF8 supplementary source offsets back to UTF16 units.
            var chars = pattern.ToCharArray();
            var charBudget = new CompileBudget(new());
            var charOk = new Pcre2SyntaxParser().TryParse(chars, profile, Pcre2Data.Instance, ref charBudget, out var charSyntax, out var charError);
            if (!charOk || charSyntax!.HasExplicitCrOrLf != expected)
            {
                failures.Add($"{id}: character convenience differs: {charError}");
            }

            if (charOk)
            {
                Assert.Equal(chars.Length, charSyntax!.SourceMap[^1]);
                Assert.Equal(charSyntax.RetainedBytes, charBudget.ReservedBytes);
                Array.Fill(chars, 'x');
                Assert.Equal(expected, charSyntax.HasExplicitCrOrLf);
            }
        }
        Assert.Equal(332, accepted);
        Assert.Equal(16, rejected);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void PerlExtendedWhitespaceAdmissionPreservesSourceBoundary()
    {
        var failures = new List<string>();
        foreach (var width in new[] { Pcre2CodeUnitWidth.Eight, Pcre2CodeUnitWidth.Sixteen })
        {
            foreach (var utf in new[] { false, true })
            {
                foreach (var space in new[] { ' ', '\t', '\n', '\v', '\f', '\r' })
                {
                    var pattern = "(?[" + space + " [a] ])";
                    var profile = new Pcre2Profile(width, utf ? Pcre2CompileFlags.Utf : 0);
                    var budget = new CompileBudget(new());
                    var ok = new Pcre2SyntaxParser().TryParse(pattern, profile, Pcre2Data.Instance, ref budget, out var syntax, out var error);
                    var expected = space is ' ' or '\t';
                    if (ok != expected || !ok && ((int)error.SourceError != 216 || error.NativeOffset != 4))
                    {
                        failures.Add($"width{width}/utf{utf}/unit{(int)space}: expected accepted={expected}, 216@4 if rejected; actual accepted={ok}, {(int)error.SourceError}@{error.NativeOffset}");
                    }

                    if (ok)
                    {
                        Assert.False(syntax!.HasExplicitCrOrLf);
                    }
                    else
                    {
                        Assert.Equal(0, budget.ReservedBytes);
                    }
                    // The same units inside an ordinary bracket operand are
                    // literals except SPACE/HT, which implied /xx ignores.
                    var nestedBudget = new CompileBudget(new());
                    Assert.True(new Pcre2SyntaxParser().TryParse("(?[ [" + space + "a] ])", profile, Pcre2Data.Instance, ref nestedBudget, out var nested, out _));
                    Assert.Equal(space is '\n' or '\r', nested!.HasExplicitCrOrLf);
                    Assert.Equal(nested.RetainedBytes, nestedBudget.ReservedBytes);
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ClassFrameGrowthAndCompileFailureRemainBudgeted()
    {
        var pattern = "(?[ [\\n] | [a] ])";
        var profile = new Pcre2Profile();
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse(pattern, profile, Pcre2Data.Instance, ref budget, out var syntax, out _));
        Assert.True(syntax!.HasExplicitCrOrLf);
        var bounded = new CompileBudget(new(maxCompileMemoryBytes: budget.PeakBytes - 1));
        Assert.False(new Pcre2SyntaxParser().TryParse(pattern, profile, Pcre2Data.Instance, ref bounded, out var failure, out var error));
        Assert.Null(failure);
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, error.Diagnostic.Code);
        Assert.Equal(0, bounded.ReservedBytes);
        // A failed parse cannot contaminate subsequent calls on the shareable parser.
        var parser = new Pcre2SyntaxParser();
        var bad = new CompileBudget(new());
        Assert.False(parser.TryParse("[\\n-", profile, Pcre2Data.Instance, ref bad, out _, out _));
        var fresh = new CompileBudget(new());
        Assert.True(parser.TryParse("\\v", profile, Pcre2Data.Instance, ref fresh, out var clean, out _));
        Assert.False(clean!.HasExplicitCrOrLf);
    }
    private static string Root()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "GregsEx.slnx")))
        {
            directory = Directory.GetParent(directory)!.FullName;
        }

        return directory;
    }
}
