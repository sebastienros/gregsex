using GregsEx.Contracts;
using GregsEx.IR;
using GregsEx.Text;
using GregsEx.Unicode.Core;
using Xunit;

namespace GregsEx.Tests.Text;

public class AllocationTests
{
    [Fact]
    public void PreparedReadersAndClassificationPreserveResultsWithPreparedStorage()
    {
        var scalarChars = ReaderTests.Profile();
        var unitChars = ReaderTests.Profile(RegularCoreMode.Utf16CodeUnits);
        var scalarBytes = ReaderTests.Profile(kind: RegexInputKind.Utf8);
        var rawBytes = ReaderTests.Profile(RegularCoreMode.RawBytes, RegexInputKind.Bytes);
        var limits = new RegexMatchLimits();
        var budget = new CompileBudget(new());
        Assert.True(CoreCharacterClasses.TryCreateAscii(AsciiClass.Word, true, scalarChars, ref budget, default, out var set, out _));
        Check<char, CharTextReader>("abc\0\uD83D\uDE00\u20AC", 0, scalarChars, limits, set!, RegexSearchStatus.NoMatch);
        Check<char, CharTextReader>("", 0, scalarChars, limits, set!, RegexSearchStatus.NoMatch);
        Check<char, CharTextReader>("\uD83D\uDE00", 1, scalarChars, limits, set!, RegexSearchStatus.InvalidInput);
        Check<char, CharTextReader>("a\uD800", 0, scalarChars, limits, set!, RegexSearchStatus.InvalidInput);
        Check<char, CharTextReader>("a\uD800\uDC00\uDFFF", 2, unitChars, limits, set!, RegexSearchStatus.NoMatch);
        Check<byte, ByteTextReader>("abc\0\uD83D\uDE00\u20AC"u8, 0, scalarBytes, limits, set!, RegexSearchStatus.NoMatch);
        Check<byte, ByteTextReader>([], 0, scalarBytes, limits, set!, RegexSearchStatus.NoMatch);
        Check<byte, ByteTextReader>("\uD83D\uDE00"u8, 1, scalarBytes, limits, set!, RegexSearchStatus.InvalidInput);
        Check<byte, ByteTextReader>([97, 0xE0, 0x80, 0x80], 0, scalarBytes, limits, set!, RegexSearchStatus.InvalidInput);
        Check<byte, ByteTextReader>([0, 97, 0x80, 0xFF], 2, rawBytes, limits, set!, RegexSearchStatus.NoMatch);
    }

    private static void Check<TUnit, TReader>(ReadOnlySpan<TUnit> input, int start, ProfileDescriptor profile,
        RegexMatchLimits limits, CharacterClass set, RegexSearchStatus expected)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        long checksum = 0;
        for (var i = 0; i < 4; i++)
        {
            checksum += Execute<TUnit, TReader>(input, start, profile, limits, set, expected);
        }

        for (var i = 0; i < 4096; i++)
        {
            checksum += Execute<TUnit, TReader>(input, start, profile, limits, set, expected);
        }

        Assert.True(checksum > 0);
    }

    private static long Execute<TUnit, TReader>(ReadOnlySpan<TUnit> input, int start, ProfileDescriptor profile,
        RegexMatchLimits limits, CharacterClass set, RegexSearchStatus expected)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        var state = new RunState(new([], [], []), limits);
        var status = TReader.Validate(input, start, profile, ref state);
        if (status != expected)
        {
            return long.MinValue / 10000;
        }

        var checksum = 1 + state.Budget.WorkConsumed;
        if (status != RegexSearchStatus.NoMatch)
        {
            return checksum;
        }

        for (var offset = 0; TReader.TryRead(input, offset, profile, out var value, out var width); offset += width)
        {
            if (!state.TryCharge(1, offset) ||
                !CoreCharacterClasses.TryContains(set, value, profile, ref state, offset, out var contains))
            {
                return long.MinValue / 10000;
            }

            checksum += value + width + (contains ? 1 : 0);
        }
        return checksum;
    }

    [Fact]
    public void PreparedIntervalAlgebraUsesOnlySuppliedCapacity()
    {
        var profile = ReaderTests.Profile();
        var limits = new RegexCompileLimits();
        CharacterRange[] left = [new(1, 10), new(100, 110)];
        CharacterRange[] right = [new(5, 15), new(105, 115)];
        Span<CharacterRange> output = stackalloc CharacterRange[8];
        long checksum = 0;
        for (var i = 0; i < 4; i++)
        {
            checksum += Algebra(left, right, output, profile, limits);
        }

        for (var i = 0; i < 4096; i++)
        {
            checksum += Algebra(left, right, output, profile, limits);
        }

        Assert.True(checksum > 0);
    }

    private static long Algebra(ReadOnlySpan<CharacterRange> left, ReadOnlySpan<CharacterRange> right,
        Span<CharacterRange> output, ProfileDescriptor profile, RegexCompileLimits limits)
    {
        var budget = new CompileBudget(limits);
        if (!CoreCharacterClasses.TryUnion(left, right, output, profile, ref budget, default, out var union, out _) ||
            !CoreCharacterClasses.TryIntersect(left, right, output, profile, ref budget, default, out var intersection, out _) ||
            !CoreCharacterClasses.TryComplement(left, output, profile, ref budget, default, out var complement, out _))
        {
            return long.MinValue / 10000;
        }

        return union + intersection + complement + budget.WorkConsumed;
    }
}
