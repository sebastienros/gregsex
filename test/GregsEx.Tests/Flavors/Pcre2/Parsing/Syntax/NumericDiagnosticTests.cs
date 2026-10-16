using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Syntax;

public sealed class NumericDiagnosticTests
{
    [Fact]
    public void RetainedNativeNumeric115Observations()
    {
        // These exact source rows contain named resolution failures; the other
        // retained 115 rows exercise the numeric syntax validation repaired here.
        // Keep the original projection and its deferred classification unchanged.
        int[] namedRows = [621, 4692, 5405, 5648, 5671, 6071, 7207, 7212, 7450, 7456, 9589, 18364, 18369, 18384];
        using var file = File.OpenRead(Path.Combine(Root(), "test/corpora/pcre2/syntax/upstream-compile-inputs.jsonl.gz"));
        using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(zip);
        var count = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            using var row = JsonDocument.Parse(line);
            var input = row.RootElement.GetProperty("input");
            var result = row.RootElement.GetProperty("result");
            if (result.GetProperty("error").GetInt32() != 115)
            {
                continue;
            }

            var member = row.RootElement.GetProperty("member").GetString()!;
            var sourceLine = row.RootElement.GetProperty("line").GetInt32();
            if (member.EndsWith("-2.jsonl", StringComparison.Ordinal) && namedRows.Contains(sourceLine))
            {
                continue;
            }

            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(),
                (Pcre2CompileFlags)input.GetProperty("options").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extraOptions").GetUInt32(),
                (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(),
                maxVariableLookbehind: input.GetProperty("maxVariableLookbehind").GetInt32());
            var units = input.GetProperty("units").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var budget = new CompileBudget(new(maxCaptureGroups: 65536));
            var accepted = profile.Width == Pcre2CodeUnitWidth.Eight ?
                new Pcre2SyntaxParser().TryParse(units.Select(x => (byte)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var diagnostic) :
                new Pcre2SyntaxParser().TryParse(units.Select(x => (char)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out syntax, out diagnostic);
            Assert.False(accepted, $"{member}:{sourceLine}: {diagnostic}");
            Assert.Null(syntax);
            Assert.Equal(115, (int)diagnostic.SourceError);
            Assert.Equal(result.GetProperty("nativeOffset").GetInt32(), diagnostic.NativeOffset);
            Assert.Equal(diagnostic.NativeOffset, diagnostic.Diagnostic.Offset);
            Assert.Equal(profile.Width == Pcre2CodeUnitWidth.Eight ? RegexOffsetUnit.Bytes : RegexOffsetUnit.Utf16CodeUnits, diagnostic.Diagnostic.LocationUnit);
            Assert.Equal(0, budget.ReservedBytes);
            count++;
        }
        Assert.Equal(78, count);
    }

    // Source pin b2bd4254, compile.c: read_number 1255-1287;
    // check_escape 1752-1792; offsets 3428, 4634, 5116, 5309, 6485.
    [Theory]
    [InlineData("\\1", 1)]
    [InlineData("\\80", 2)]
    [InlineData("\\g1", 2)]
    [InlineData("\\g+1", 3)]
    [InlineData("\\g{1}", 4)]
    [InlineData("\\g{ 10 \t}", 8)]
    [InlineData("\\g{+1}", 5)]
    [InlineData("\\g<1>", 4)]
    [InlineData("\\g'1'", 4)]
    [InlineData("\\g<+1>", 5)]
    [InlineData("(?1)", 3)]
    [InlineData("(?+1)", 4)]
    [InlineData("(?(10))", 3)]
    [InlineData("(?(+10))", 4)]
    [InlineData("(?(1))", 2)]
    [InlineData("(?(R10))", 3)]
    [InlineData("(*scs:(1)a)", 7)]
    [InlineData("(*scs:(+1)a)", 7)]
    [InlineData("(*scs:(0)a)", 8)]
    [InlineData("\\g{0}", 5)]
    [InlineData("\\g0", 3)]
    [InlineData("(?(0))", 4)]
    [InlineData("(?-1)", 4)]
    [InlineData("(?(-1))", 5)]
    [InlineData("\\g-1", 4)]
    [InlineData("\\g{-1}", 2)]
    [InlineData("\\g<-1>", 2)]
    [InlineData("\\g'-1'", 2)]
    public void SourceRulesPreserveFourDomainsAndCharByteMaps(string suffix, int offset)
    {
        var domains = new (Pcre2Profile Profile, string Prefix, int NativeLength)[]
        {
            (new(Pcre2CodeUnitWidth.Eight),"é",1),
            (new(Pcre2CodeUnitWidth.Eight,Pcre2CompileFlags.Utf),"😀",4),
            (new(Pcre2CodeUnitWidth.Sixteen),"\uD800",1),
            (new(Pcre2CodeUnitWidth.Sixteen,Pcre2CompileFlags.Utf),"😀",2)
        };
        foreach (var (profile, prefix, nativeLength) in domains)
        {
            var pattern = prefix + suffix;
            var budget = new CompileBudget(new());
            Assert.False(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var d));
            Assert.Null(syntax);
            Assert.Equal(115, (int)d.SourceError);
            Assert.Equal(nativeLength + offset, d.NativeOffset);
            Assert.Equal(prefix.Length + offset, d.Diagnostic.Offset);
            Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, d.Diagnostic.LocationUnit);
            Assert.Equal(0, budget.ReservedBytes);
            if (profile.Width != Pcre2CodeUnitWidth.Eight)
            {
                continue;
            }

            var bytes = profile.Utf ? Encoding.UTF8.GetBytes(pattern) : [.. pattern.Select(c => (byte)c)];
            budget = new(new());
            Assert.False(new Pcre2SyntaxParser().TryParse(bytes, profile, Pcre2Data.Instance, ref budget, out syntax, out d));
            Assert.Null(syntax);
            Assert.Equal(115, (int)d.SourceError);
            Assert.Equal(nativeLength + offset, d.NativeOffset);
            Assert.Equal(d.NativeOffset, d.Diagnostic.Offset);
            Assert.Equal(RegexOffsetUnit.Bytes, d.Diagnostic.LocationUnit);
            Assert.Equal(0, budget.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("\\1(a)", (int)Pcre2ReferenceKind.Absolute, 1, 0)]
    [InlineData("\\g{1}(a)", (int)Pcre2ReferenceKind.Absolute, 1, 0)]
    [InlineData("(?1)(a)", (int)Pcre2ReferenceKind.Absolute, 1, 0)]
    [InlineData("(?+1)(a)", (int)Pcre2ReferenceKind.Relative, 1, 1)]
    [InlineData("(a)(?-1)", (int)Pcre2ReferenceKind.Relative, -1, 1)]
    [InlineData("(?(+1)a|b)(c)", (int)Pcre2ReferenceKind.Relative, 1, 1)]
    [InlineData("(*scs:(+1)a)(a)", (int)Pcre2ReferenceKind.Relative, 1, 1)]
    [InlineData("(?|(a)(b)|(c)\\g{-1})", (int)Pcre2ReferenceKind.Relative, -1, 1)]
    [InlineData("(?0)", (int)Pcre2ReferenceKind.WholePattern, 0, 0)]
    [InlineData("\\g<0>", (int)Pcre2ReferenceKind.WholePattern, 0, 0)]
    public void ValidReferencesKeepLexicalCandidatesAndAst(string pattern, int kind, int number, int candidate)
    {
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), new(), Pcre2Data.Instance, ref budget, out var syntax, out var d));
        Assert.NotNull(syntax);
        Assert.Equal(Pcre2PlanStage.Syntax, syntax.Stage);
        Assert.Equal(Pcre2DiagnosticKind.None, d.Kind);
        var reference = Assert.Single(syntax.References.ToArray());
        Assert.Equal((Pcre2ReferenceKind)kind, reference.Kind);
        Assert.Equal(number, reference.Number);
        Assert.Equal(candidate == 0 ? 0 : 1, reference.Candidates.Count);
        if (candidate != 0)
        {
            Assert.Equal(candidate, syntax.Numbers[reference.Candidates.Offset]);
        }

        Assert.Equal(Enumerable.Range(0, pattern.Length + 1), syntax.SourceMap.ToArray());
        Assert.Equal(syntax.RetainedBytes, budget.ReservedBytes);
    }

    [Fact]
    public void UnknownNamesStillReachLowering()
    {
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse("(?P>abc)".AsSpan(), new(), Pcre2Data.Instance, ref budget, out var syntax, out _));
        Assert.NotNull(syntax);
        Assert.Equal(Pcre2ReferenceKind.Named, Assert.Single(syntax.References.ToArray()).Kind);
        Assert.Equal(0, syntax.Names[0].Numbers.Count);
    }

    private static string Root()
    { var p = AppContext.BaseDirectory; while (p != null) { if (File.Exists(Path.Combine(p, "GregsEx.slnx"))) { return p; } p = Directory.GetParent(p)?.FullName; } throw new InvalidOperationException(); }
}
