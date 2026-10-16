using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Syntax;

public sealed class OptionMetadataTests
{
    [Theory]
    [InlineData("", 144, 16)]
    [InlineData("c-", 60, 16)]
    public void PinnedNativeLexicalInfoSurvivesSyntaxAndRealCompilerInFourDomains(string corpus, int acceptedCount, int rejectedCount)
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "GregsEx.slnx")))
        {
            directory = Directory.GetParent(directory)!.FullName;
        }

        var report = Path.Combine(directory, "test/corpora/pcre2/option-metadata");
        using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, $"native-{corpus}inputs.json")));
        var observed = new Dictionary<string, JsonElement>();
        foreach (var width in new[] { 8, 16 })
        {
            foreach (var line in File.ReadLines(Path.Combine(report, $"native-{corpus}{width}.jsonl")))
            {
                using var row = JsonDocument.Parse(line);
                observed.Add(row.RootElement.GetProperty("id").GetString()!, row.RootElement.Clone());
            }
        }

        int accepted = 0, rejected = 0;
        var failures = new List<string>();
        var parser = new Pcre2SyntaxParser();
        var compiler = new Pcre2Compiler();
        foreach (var row in input.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            var native = observed[id];
            var width = (Pcre2CodeUnitWidth)row.GetProperty("width").GetInt32();
            var profile = new Pcre2Profile(width, (Pcre2CompileFlags)row.GetProperty("flags").GetUInt32());
            var units = row.GetProperty("units").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var bytes = units.Select(x => (byte)x).ToArray();
            var chars = units.Select(x => (char)x).ToArray();
            var syntaxBudget = new CompileBudget(new());
            var parsed = width == Pcre2CodeUnitWidth.Eight
                ? parser.TryParse(bytes, profile, Pcre2Data.Instance, ref syntaxBudget, out var syntax, out var diagnostic)
                : parser.TryParse(chars, profile, Pcre2Data.Instance, ref syntaxBudget, out syntax, out diagnostic);
            var compileBudget = new CompileBudget(new());
            Assert.True(compileBudget.TryReserveBytes(97, default, out _));
            var compiled = width == Pcre2CodeUnitWidth.Eight
                ? compiler.TryCompile(bytes, profile, Pcre2Data.Instance, ref compileBudget, out var executable, out var compileError)
                : compiler.TryCompile(chars, profile, Pcre2Data.Instance, ref compileBudget, out executable, out compileError);
            if (!native.GetProperty("accepted").GetBoolean())
            {
                rejected++;
                int error = native.GetProperty("error").GetInt32(), offset = native.GetProperty("offset").GetInt32();
                if (parsed || compiled || (int)diagnostic.SourceError != error || diagnostic.NativeOffset != offset ||
                    (int)compileError.SourceError != error || compileError.NativeOffset != offset)
                {
                    failures.Add($"{id}: native {error}@{offset}; syntax {parsed}/{diagnostic}; compiler {compiled}/{compileError}");
                }

                Assert.Null(syntax);
                Assert.Null(executable);
                Assert.Equal(0, syntaxBudget.ReservedBytes);
                Assert.Equal(97, compileBudget.ReservedBytes);
                continue;
            }
            accepted++;
            var expected = native.GetProperty("jchanged").GetInt32() != 0;
            var expectedC = native.TryGetProperty("hasBackslashC", out var c) && c.GetInt32() != 0;
            if (!parsed || !compiled)
            {
                failures.Add($"{id}: syntax {parsed}/{diagnostic}; compiler {compiled}/{compileError}");
                continue;
            }
            Assert.Equal(default, diagnostic);
            Assert.Equal(default, compileError);
            Assert.False(syntax!.IsExecutable);
            Assert.True(executable!.IsExecutable);
            if (syntax.HasDuplicateNameOptionChange != expected || executable.HasDuplicateNameOptionChange != expected)
            {
                failures.Add($"{id}: native {expected}; syntax {syntax.HasDuplicateNameOptionChange}; executable {executable.HasDuplicateNameOptionChange}");
            }

            Assert.Equal(expectedC, syntax.HasBackslashC);
            Assert.Equal(expectedC, executable.HasBackslashC);
            Assert.Equal(syntax.RetainedBytes, syntaxBudget.ReservedBytes);
            Assert.Equal(97 + executable.RetainedBytes, compileBudget.ReservedBytes);
            Assert.Equal(profile.Flags, executable.ArgumentFlags);
            // The char convenience route and copied plans have the same lexical metadata.
            var charBudget = new CompileBudget(new());
            var convenience = row.GetProperty("pattern").GetString()!.ToCharArray();
            Assert.True(compiler.TryCompile(convenience, profile, Pcre2Data.Instance, ref charBudget,
                out var charPlan, out var charError), $"{id}: {charError}");
            Assert.Equal(expected, charPlan!.HasDuplicateNameOptionChange);
            Assert.Equal(expectedC, charPlan.HasBackslashC);
            Array.Fill(bytes, (byte)'x');
            Array.Fill(chars, 'x');
            Array.Fill(convenience, 'x');
            Assert.Equal(expected, syntax.HasDuplicateNameOptionChange);
            Assert.Equal(expected, executable.HasDuplicateNameOptionChange);
            Assert.Equal(expected, charPlan.HasDuplicateNameOptionChange);
            Assert.Equal(expectedC, syntax.HasBackslashC);
            Assert.Equal(expectedC, executable.HasBackslashC);
            Assert.Equal(expectedC, charPlan.HasBackslashC);
        }
        Assert.Equal(acceptedCount, accepted);
        Assert.Equal(rejectedCount, rejected);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void RawZeroRepeatRetainsBackslashCWithoutExecutableCodeUnitNode(int width)
    {
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2Compiler().TryCompile("\\C{0}", new((Pcre2CodeUnitWidth)width),
            Pcre2Data.Instance, ref budget, out var plan, out var error), error.ToString());
        Assert.True(plan!.HasBackslashC);
        Assert.DoesNotContain(plan.Nodes.ToArray(), node => node.Kind == Pcre2NodeKind.CodeUnit);
        Assert.Equal(plan.RetainedBytes, budget.ReservedBytes);
    }

    [Fact]
    public void FailedCompileReleasesStorageAndCannotContaminateLaterMetadata()
    {
        var compiler = new Pcre2Compiler();
        const string pattern = "(?(DEFINE)(?J:(?<x>\\C)))b";
        var budget = new CompileBudget(new());
        Assert.True(compiler.TryCompile(pattern, new(), Pcre2Data.Instance, ref budget, out var plan, out _));
        Assert.True(plan!.HasDuplicateNameOptionChange);
        Assert.True(plan.HasBackslashC);
        var bounded = new CompileBudget(new(maxCompileMemoryBytes: budget.PeakBytes - 1));
        Assert.False(compiler.TryCompile(pattern, new(), Pcre2Data.Instance, ref bounded, out var rejected, out var error));
        Assert.Null(rejected);
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, error.Diagnostic.Code);
        Assert.Equal(0, bounded.ReservedBytes);
        var invalid = new CompileBudget(new());
        Assert.False(compiler.TryCompile("(?Jq)", new(), Pcre2Data.Instance, ref invalid, out _, out _));
        Assert.Equal(0, invalid.ReservedBytes);
        var fresh = new CompileBudget(new());
        Assert.True(compiler.TryCompile("a", new(flags: Pcre2CompileFlags.Dupnames), Pcre2Data.Instance,
            ref fresh, out var clean, out _));
        Assert.False(clean!.HasDuplicateNameOptionChange);
        Assert.False(clean.HasBackslashC);
    }
}
