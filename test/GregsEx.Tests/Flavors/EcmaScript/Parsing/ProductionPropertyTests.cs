using GregsEx.Contracts;
using GregsEx.Flavors.EcmaScript.Parsing;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

// Counts and positive strings are independently specified by the frozen Emoji16
// emoji-sequences/emoji-zwj-sequences inputs. RGI is their union, not a block test.
public sealed class ProductionPropertyTests(ITestOutputHelper output)
{
    public static TheoryData<string, int, string> Properties => new()
    {
        { "Basic_Emoji", 207, "\u2764\uFE0F" },
        { "Emoji_Keycap_Sequence", 12, "1\uFE0F\u20E3" },
        { "RGI_Emoji_Flag_Sequence", 259, "\U0001F1FA\U0001F1F8" },
        { "RGI_Emoji_Modifier_Sequence", 655, "\U0001F44D\U0001F3FD" },
        { "RGI_Emoji_Tag_Sequence", 3, "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F" },
        { "RGI_Emoji_ZWJ_Sequence", 1468, "\U0001F469\u200D\U0001F4BB" },
        { "RGI_Emoji", 2604, "\U0001F469\u200D\U0001F4BB" }
    };
    [Theory, MemberData(nameof(Properties))]
    public void RealPropertiesCompileAtDefaultAndFiniteExpandedWork(string property, int count, string positive)
    {
        var allDefaultsSucceeded = true;
        Span<byte> integer = stackalloc byte[4];
        foreach (var flags in new[] { "v", "vi" })
        {
            foreach (var work in new[] { 10_000_000L, 100_000_000L })
            {
                var budget = new CompileBudget(new(maxCompileWork: work));
                var unicode = Composition.Data(ref budget);
                var success = new EcmaCompiler().TryCompile(@"\p{" + property + "}", GrammarTests.Profile(flags), unicode,
                    ref budget, out var program, out var diagnostic);
                output.WriteLine($"property={property};flags={flags};ceiling={work};success={success};code={diagnostic.Diagnostic.Code};work={budget.WorkConsumed};retained={budget.ReservedBytes};peak={budget.PeakBytes}");
                if (work == 10_000_000)
                {
                    allDefaultsSucceeded &= success;
                }
                else
                {
                    Assert.True(success, diagnostic.ToString());
                }

                if (success)
                {
                    Assert.Equal(count, program!.Strings.Length);
                    var (Flags, Name, Singles, Strings, IntervalsSha256, StringsSha256) = ProductionStringExpectations.Values.Single(x => x.Name == property && x.Flags == flags);
                    using var ranges = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var strings = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (var r in program.Intervals)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(integer, r.First);
                        ranges.AppendData(integer);
                        BinaryPrimitives.WriteInt32LittleEndian(integer, r.Last);
                        ranges.AppendData(integer);
                    }
                    foreach (var s in program.Strings)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(integer, s.Length);
                        strings.AppendData(integer);
                        foreach (var point in program.StringCodePoints.Slice(s.Offset, s.Length))
                        { BinaryPrimitives.WriteInt32LittleEndian(integer, point); strings.AppendData(integer); }
                    }
                    Assert.Equal(IntervalsSha256, Convert.ToHexStringLower(ranges.GetHashAndReset()));
                    Assert.Equal(StringsSha256, Convert.ToHexStringLower(strings.GetHashAndReset()));
                }
                Assert.True(budget.PeakBytes >= budget.ReservedBytes);
            }
            var composed = new Composition(@"^\p{" + property + "}$", flags, new(maxCompileWork: 100_000_000));
            composed.Expect(positive, 0, positive.Length);
            Assert.Equal(RegexSearchStatus.NoMatch, composed.Search("A"));
            Assert.Equal(RegexSearchStatus.NoMatch, composed.Search(positive + "A"));
            Assert.Equal(RegexSearchStatus.NoMatch, composed.Search("\uD800"));
        }
        Assert.True(allDefaultsSucceeded, "Simple real string property must compile within unchanged default finite work.");
    }
    public static IEnumerable<TheoryDataRow<string>> PropertyNames() => ProductionStringExpectations.Values.Select(x => x.Name).Distinct().Select(x => new TheoryDataRow<string>(x));
    [Theory, MemberData(nameof(PropertyNames))]
    public void StringPropertiesRejectOtherModesNegationAndStaticStringComplement(string property)
    {
        foreach (var pattern in new[] { @"\P{" + property + "}", @"[^\p{" + property + "}]", @"[^\p{" + property + @"}--\p{" + property + "}]" })
        {
            Reject(pattern, "v");
        }

        Reject(@"\p{" + property + "}", "u");
        Reject(@"\p{" + property.ToLowerInvariant() + "}", "v");
        Reject(@"\p{" + property + "=True}", "v");
    }
    private static void Reject(string pattern, string flags)
    {
        var budget = new CompileBudget(new());
        var unicode = Composition.Data(ref budget);
        var before = budget.ReservedBytes;
        Assert.False(new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile(flags), unicode, ref budget, out var p, out var d));
        Assert.Null(p);
        Assert.Equal(RegexDiagnosticCode.InvalidSyntax, d.Diagnostic.Code);
        Assert.Equal(before, budget.ReservedBytes);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, d.Diagnostic.LocationUnit);
        Assert.InRange(d.Diagnostic.Offset, 0, pattern.Length);
        Assert.InRange(d.Diagnostic.Length, 0, pattern.Length - d.Diagnostic.Offset);
    }
}
