using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;
using GregsEx.IR;
using Xunit;

namespace GregsEx.Tests.Flavors.XmlShared.Semantics;

public class AlgebraTests
{
    private static readonly SourceRange Source = new(17, 3, RegexOffsetUnit.Utf16CodeUnits);
    private static XmlInterval[] Apply(XmlSetOperation operation, XmlInterval[] left, XmlInterval[]? right = null)
    {
        var budget = new CompileBudget(new());
        Assert.True(new XmlClassAlgebra().TryApply(operation, left, right ?? [], DataTests.Data(), ref budget, Source, out var result, out var diagnostic), diagnostic.ToString());
        Assert.Equal(result!.Length == 0 ? 0 : 32 + 8L * result.Length, budget.ReservedBytes);
        for (var i = 0; i < result.Length; i++)
        {
            Assert.True(XmlAlphabet.ContainsRange(result[i]));
            if (i > 0)
            {
                Assert.True(result[i].First > result[i - 1].Last + 1);
            }
        }
        return result;
    }
    [Fact]
    public void UnionNormalizesUnsortedDuplicateAndAdjacentInputsAndClipsXmlAlphabet()
    {
        var result = Apply(XmlSetOperation.Union, [new(100, 110), new(0, 32), new(104, 120), new(0xD7FE, 0xE001)], [new(121, 121), new(0xFFFD, 0x10001)]);
        Assert.Equal(new XmlInterval[] { new(9, 10), new(13, 13), new(32, 32), new(100, 121), new(0xD7FE, 0xD7FF), new(0xE000, 0xE001), new(0xFFFD, 0xFFFD), new(0x10000, 0x10001) }, result);
    }
    [Fact]
    public void SubtractionComplementAndNestedNegativeClassKeepLegalSingleScalarDomain()
    {
        Assert.Equal(new XmlInterval[] { new('b', 'd'), new('f', 'h'), new('j', 'n'), new('p', 't'), new('v', 'z') },
            Apply(XmlSetOperation.Subtract, [new('a', 'z')], [new('a', 'a'), new('e', 'e'), new('i', 'i'), new('o', 'o'), new('u', 'u')]));
        Assert.Empty(Apply(XmlSetOperation.Subtract, [new(32, 40)], [new(0, 0x10FFFF)]));
        var all = Apply(XmlSetOperation.Complement, []);
        Assert.Equal(DataTests.Data().Alphabet.ToArray(), all);
        Assert.Empty(Apply(XmlSetOperation.Complement, all));
        var negative = Apply(XmlSetOperation.Complement, [new('a', 'z')]);
        var subtract = Apply(XmlSetOperation.Subtract, negative, [new('0', '9')]);
        Assert.False(XmlUnicodeData.Contains(subtract, 'a'));
        Assert.False(XmlUnicodeData.Contains(subtract, '5'));
        Assert.True(XmlUnicodeData.Contains(subtract, 0x10FFFF));
        Assert.False(XmlUnicodeData.Contains(subtract, 0));
    }
    [Fact]
    public void CaseVariantsExpandLiteralRangesBeforeComplementAndDoNotCloseTransitively()
    {
        var i = Apply(XmlSetOperation.CaseVariants, [new('i', 'i')]);
        Assert.True(XmlUnicodeData.Contains(i, 'I'));
        Assert.True(XmlUnicodeData.Contains(i, 0x131));
        var theta = Apply(XmlSetOperation.CaseVariants, [new(0x3D1, 0x3D1)]);
        Assert.True(XmlUnicodeData.Contains(theta, 0x398));
        Assert.False(XmlUnicodeData.Contains(theta, 0x3F4));
        var capital = Apply(XmlSetOperation.CaseVariants, [new('I', 'I')]);
        Assert.True(XmlUnicodeData.Contains(capital, 0x131));
        var sharp = Apply(XmlSetOperation.CaseVariants, [new(0xDF, 0xDF)]);
        Assert.True(XmlUnicodeData.Contains(sharp, 0x1E9E));
        Assert.False(XmlUnicodeData.Contains(sharp, 's'));
        var supplementary = Apply(XmlSetOperation.CaseVariants, [new(0x10400, 0x10400)]);
        Assert.True(XmlUnicodeData.Contains(supplementary, 0x10428));
        var negated = Apply(XmlSetOperation.Complement, Apply(XmlSetOperation.CaseVariants, [new('a', 'z')]));
        Assert.False(XmlUnicodeData.Contains(negated, 'A'));
        Assert.False(XmlUnicodeData.Contains(negated, 'a'));
    }
    [Fact]
    public void DeterministicRandomAlgebraMatchesIndependentFiniteMembershipOracle()
    {
        var random = new Random(0x584D4C);
        for (var trial = 0; trial < 200; trial++)
        {
            XmlInterval[] a = RandomRanges(random), b = RandomRanges(random);
            foreach (var operation in new[] { XmlSetOperation.Union, XmlSetOperation.Subtract, XmlSetOperation.Complement })
            {
                var result = Apply(operation, a, b);
                for (var cp = 0; cp < 256; cp++)
                {
                    bool left = Member(a, cp), right = Member(b, cp), legal = XmlAlphabet.Contains(cp);
                    var expected = legal && (operation == XmlSetOperation.Union ? left || right : operation == XmlSetOperation.Subtract ? left && !right : !left);
                    Assert.Equal(expected, XmlUnicodeData.Contains(result, cp));
                }
            }
        }
        static XmlInterval[] RandomRanges(Random r)
        {
            var ranges = new XmlInterval[r.Next(0, 12)];
            for (var i = 0; i < ranges.Length; i++)
            { int a = r.Next(256), b = r.Next(256); ranges[i] = new(Math.Min(a, b), Math.Max(a, b)); }
            return ranges;
        }
        static bool Member(XmlInterval[] ranges, int cp)
        { foreach (var r in ranges) { if (cp >= r.First && cp <= r.Last) { return true; } } return false; }
    }
    [Fact]
    public void EverySingleCaseRowExpandsExactlyTheSourceRelation()
    {
        var data = DataTests.Data();
        var rows = data.CaseMappings;
        // Every row is a distinct scalar; exhaust every sparse source singleton.
        foreach (var seed in rows)
        {
            var result = Apply(XmlSetOperation.CaseVariants, [new(seed.Scalar, seed.Scalar)]);
            foreach (var candidate in rows)
            {
                var expected = candidate.LowerKey == seed.LowerKey || candidate.UpperKey == seed.UpperKey;
                Assert.Equal(expected, XmlUnicodeData.Contains(result, candidate.Scalar));
            }
        }
    }
    [Fact]
    public void PeakRetainedClassAndCumulativeWorkFailuresReleaseScratchAndPreserveSource()
    {
        var data = DataTests.Data();
        foreach (var entry in new[]{
            (new RegexCompileLimits(maxCompileMemoryBytes:1),RegexDiagnosticCode.CompileMemoryLimitExceeded),
            (new RegexCompileLimits(maxPlanBytes:1),RegexDiagnosticCode.PlanLimitExceeded),
            (new RegexCompileLimits(maxClassRanges:1),RegexDiagnosticCode.ClassLimitExceeded),
            (new RegexCompileLimits(maxCompileWork:1),RegexDiagnosticCode.CompileWorkLimitExceeded)})
        {
            var budget = new CompileBudget(entry.Item1);
            Assert.True(budget.TryReserveBytes(1, Source, out _));
            Assert.False(new XmlClassAlgebra().TryApply(XmlSetOperation.Union, [new(0, 0x10FFFF)], [], data, ref budget, Source, out var result, out var diagnostic));
            Assert.Null(result);
            Assert.Equal(entry.Item2, diagnostic.Code);
            Assert.Equal(17, diagnostic.Offset);
            Assert.Equal(3, diagnostic.Length);
            Assert.Equal(1, budget.ReservedBytes);
        }
        var success = new CompileBudget(new());
        var algebra = new XmlClassAlgebra();
        Assert.True(algebra.TryApply(XmlSetOperation.Union, [new(32, 40)], [], data, ref success, Source, out _, out _));
        var retained = success.ReservedBytes;
        Assert.True(algebra.TryApply(XmlSetOperation.Subtract, [new(50, 60)], [new(50, 60)], data, ref success, Source, out var empty, out _));
        Assert.Empty(empty!);
        Assert.Equal(retained, success.ReservedBytes);
    }
    [Fact]
    public void WorkAndPeakLimitsInterruptCaseExpansionWithoutPublishingOrLeaking()
    {
        var data = DataTests.Data();
        foreach (var limits in new[] { new RegexCompileLimits(maxCompileWork: 100), new RegexCompileLimits(maxCompileMemoryBytes: 100) })
        {
            var budget = new CompileBudget(limits);
            Assert.False(new XmlClassAlgebra().TryApply(XmlSetOperation.CaseVariants, [new('a', 'z')], [], data, ref budget, Source, out var result, out _));
            Assert.Null(result);
            Assert.Equal(0, budget.ReservedBytes);
        }
    }
}
