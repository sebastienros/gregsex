using GregsEx.Contracts;
using GregsEx.IR;
using GregsEx.Unicode.Core;
using Xunit;

namespace GregsEx.Tests.Text;

public class ClassTests
{
    [Theory]
    [InlineData(RegularCoreMode.UnicodeScalars, RegexInputKind.Utf16)]
    [InlineData(RegularCoreMode.Utf16CodeUnits, RegexInputKind.Utf16)]
    [InlineData(RegularCoreMode.RawBytes, RegexInputKind.Bytes)]
    public void AsciiSetsAndComplementsUseOnlyTheLegalAlphabet(RegularCoreMode mode, RegexInputKind kind)
    {
        var profile = ReaderTests.Profile(mode, kind);
        foreach (var ascii in Enum.GetValues<AsciiClass>())
        {
            foreach (var negated in new[] { false, true })
            {
                var budget = new CompileBudget(new());
                Assert.True(CoreCharacterClasses.TryCreateAscii(ascii, negated, profile, ref budget, default, out var set, out _));
                for (var value = -1; value <= 256; value++)
                {
                    var baseMember = ascii switch
                    {
                        AsciiClass.Digit => value >= 48 && value <= 57,
                        AsciiClass.Word => value >= 48 && value <= 57 || value >= 65 && value <= 90 ||
                            value >= 97 && value <= 122 || value == 95,
                        _ => value >= 9 && value <= 13 || value == 32
                    };
                    var state = new RunState(new([], [], []), new());
                    Assert.True(CoreCharacterClasses.TryContains(set!, value, profile, ref state, 7, out var found));
                    Assert.Equal(profile.Contains(value) && (negated ? !baseMember : baseMember), found);
                    Assert.Equal(baseMember, ascii switch
                    {
                        AsciiClass.Digit => CoreCharacterClasses.IsAsciiDigit(value),
                        AsciiClass.Word => CoreCharacterClasses.IsAsciiWord(value),
                        _ => CoreCharacterClasses.IsAsciiSpace(value)
                    });
                }
                foreach (var value in new[] { 0xD800, 0xFFFF, 0x10000, 0x10FFFF, 0x110000 })
                {
                    var state = new RunState(new([], [], []), new());
                    Assert.True(CoreCharacterClasses.TryContains(set!, value, profile, ref state, 0, out var found));
                    Assert.Equal(negated && profile.Contains(value), found);
                }
            }
        }
    }

    [Fact]
    public void ClassificationChargesComparisonsAndNeverReturnsPartialSuccess()
    {
        var profile = ReaderTests.Profile();
        var budget = new CompileBudget(new());
        Assert.True(CharacterClass.TryCreate([new(1, 2), new(4, 5), new(8, 9)], true,
            profile, ref budget, default, out var set, out _));
        var state = new RunState(new([], [], []), new(2));
        Assert.False(CoreCharacterClasses.TryContains(set!, 8, profile, ref state, 17, out var found));
        Assert.False(found);
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, 17, 2), state.Failure);
        var reused = new RunState(new([], [], []), new());
        Assert.True(CoreCharacterClasses.TryContains(set!, 6, profile, ref reused, 0, out found));
        Assert.True(found);
        Assert.Equal(3, reused.Budget.WorkConsumed);
    }

    [Fact]
    public void ScalarRangeCrossingSurrogateHoleDoesNotClassifySurrogates()
    {
        var profile = ReaderTests.Profile();
        var budget = new CompileBudget(new());
        Assert.True(CharacterClass.TryCreate([new(0, 0x10FFFF)], false, profile, ref budget, default, out var set, out _));
        foreach (var value in new[] { 0xD7FF, 0xD800, 0xDFFF, 0xE000, 0x10FFFF })
        {
            var state = new RunState(new([], [], []), new());
            Assert.True(CoreCharacterClasses.TryContains(set!, value, profile, ref state, 0, out var found));
            Assert.Equal(value is not (>= 0xD800 and <= 0xDFFF), found);
        }
    }

    [Fact]
    public void AlgebraCanonicalizesUnionIntersectionAndLegalComplement()
    {
        CharacterRange[] left = [new(1, 3), new(8, 10)];
        CharacterRange[] right = [new(3, 8), new(20, 20)];
        Span<CharacterRange> output = stackalloc CharacterRange[8];
        var profile = ReaderTests.Profile(RegularCoreMode.RawBytes, RegexInputKind.Bytes);
        var budget = new CompileBudget(new());
        Assert.True(CoreCharacterClasses.TryUnion(left, right, output, profile, ref budget, default, out var count, out _));
        Assert.Equal([new CharacterRange(1, 10), new(20, 20)], output[..count].ToArray());
        Assert.True(CoreCharacterClasses.TryIntersect(left, right, output, profile, ref budget, default, out count, out _));
        Assert.Equal([new CharacterRange(3, 3), new(8, 8)], output[..count].ToArray());
        Assert.True(CoreCharacterClasses.TryComplement(left, output, profile, ref budget, default, out count, out _));
        Assert.Equal([new CharacterRange(0, 0), new(4, 7), new(11, 255)], output[..count].ToArray());
        Assert.True(CoreCharacterClasses.TryComplement([], output, ReaderTests.Profile(), ref budget, default, out count, out _));
        Assert.Equal([new CharacterRange(0, 0xD7FF), new(0xE000, 0x10FFFF)], output[..count].ToArray());
        Assert.True(CoreCharacterClasses.TryComplement([new(0, 0x10FFFF)], output, ReaderTests.Profile(), ref budget, default, out count, out _));
        Assert.Equal(0, count);
        Assert.True(CoreCharacterClasses.TryComplement([new(0xD801, 0xDFFE)], output, ReaderTests.Profile(), ref budget, default, out count, out _));
        Assert.Equal([new CharacterRange(0, 0xD7FF), new(0xE000, 0x10FFFF)], output[..count].ToArray());
    }

    [Fact]
    public void SeededRawAlgebraAgreesWithIndependentBitSets()
    {
        var random = new Random(41011);
        var profile = ReaderTests.Profile(RegularCoreMode.RawBytes, RegexInputKind.Bytes);
        Span<CharacterRange> output = stackalloc CharacterRange[256];
        for (var test = 0; test < 100; test++)
        {
            bool[] a = new bool[256], b = new bool[256];
            for (var i = 0; i < 256; i++)
            { a[i] = random.Next(4) == 0; b[i] = random.Next(4) == 0; }
            var left = ToRanges(a);
            var right = ToRanges(b);
            var budget = new CompileBudget(new());
            Assert.True(CoreCharacterClasses.TryUnion(left, right, output, profile, ref budget, default, out var count, out _));
            for (var i = 0; i < 256; i++)
            {
                Assert.Equal(a[i] || b[i], Contains(output[..count], i));
            }

            Assert.True(CoreCharacterClasses.TryIntersect(left, right, output, profile, ref budget, default, out count, out _));
            for (var i = 0; i < 256; i++)
            {
                Assert.Equal(a[i] && b[i], Contains(output[..count], i));
            }

            Assert.True(CoreCharacterClasses.TryComplement(left, output, profile, ref budget, default, out count, out _));
            for (var i = 0; i < 256; i++)
            {
                Assert.Equal(!a[i], Contains(output[..count], i));
            }
        }
    }

    [Fact]
    public void AlgebraRejectsCapacityWorkAndInvalidRangesWithoutAllocatingStorage()
    {
        var profile = ReaderTests.Profile();
        Span<CharacterRange> output = stackalloc CharacterRange[2];
        var source = new SourceRange(12, 3, RegexOffsetUnit.Bytes);
        var budget = new CompileBudget(new());
        Assert.False(CoreCharacterClasses.TryComplement([], output[..1], profile, ref budget, source, out var count, out var diagnostic));
        Assert.Equal(0, count);
        Assert.Equal(new RegexDiagnostic(RegexDiagnosticCode.ClassLimitExceeded, 12, 3, RegexOffsetUnit.Bytes), diagnostic);
        budget = new(new(maxCompileWork: 1));
        Assert.False(CoreCharacterClasses.TryUnion([new(1, 1)], [], output, profile, ref budget, source, out count, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, diagnostic.Code);
        budget = new(new(maxClassRanges: 1));
        Assert.False(CoreCharacterClasses.TryUnion([new(1, 1), new(3, 3)], [], output, profile, ref budget, source, out count, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.ClassLimitExceeded, diagnostic.Code);
        budget = new(new());
        Assert.False(CoreCharacterClasses.TryUnion([new(3, 2)], [], output, profile, ref budget, source, out count, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
        var memory = new CompileBudget(new(maxCompileMemoryBytes: 71));
        Assert.False(CoreCharacterClasses.TryCreateAscii(AsciiClass.Digit, false, profile, ref memory, source, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, diagnostic.Code);
    }

    private static CharacterRange[] ToRanges(bool[] values)
    {
        var ranges = new List<CharacterRange>();
        for (var i = 0; i < values.Length; i++)
        {
            if (!values[i])
            {
                continue;
            }

            var start = i;
            while (i + 1 < values.Length && values[i + 1])
            {
                i++;
            }

            ranges.Add(new(start, i));
        }
        return [.. ranges];
    }

    [Fact]
    public void AlgebraRejectsOverlappingOutputBeforeWriting()
    {
        CharacterRange[] ranges = [new(1, 2), new(3, 4)];
        var before = (CharacterRange[])ranges.Clone();
        Assert.Throws<ArgumentException>(() => Overlap(ranges));
        Assert.Equal(before, ranges);
    }

    private static void Overlap(CharacterRange[] ranges)
    {
        var budget = new CompileBudget(new());
        CoreCharacterClasses.TryUnion(ranges, [], ranges, ReaderTests.Profile(), ref budget, default, out _, out _);
    }
    private static bool Contains(ReadOnlySpan<CharacterRange> ranges, int value)
    {
        foreach (var range in ranges)
        {
            if (value >= range.First && value <= range.Last)
            {
                return true;
            }
        }

        return false;
    }
}
