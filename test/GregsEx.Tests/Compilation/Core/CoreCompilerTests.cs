using GregsEx.Compilation.Core;
using GregsEx.Contracts;
using GregsEx.IR;
using Xunit;

namespace GregsEx.Tests.Compilation.Core;

public sealed class CoreCompilerTests
{
    private static ProfileDescriptor Profile(RegularCoreMode mode = RegularCoreMode.UnicodeScalars,
        RegularCoreFlags flags = RegularCoreFlags.None)
    {
        var input = mode == RegularCoreMode.RawBytes ? RegexInputKind.Bytes : RegexInputKind.Utf16;
        Assert.True(ProfileDescriptor.TryCreate(new RegularCoreProfile(mode, flags), input, out var profile, out _));
        return profile!;
    }

    private static SemanticProgram Compile(string pattern, RegularCoreMode mode = RegularCoreMode.UnicodeScalars,
        RegexCompileLimits? limits = null, RegularCoreFlags flags = RegularCoreFlags.None)
    {
        var budget = new CompileBudget(limits ?? new());
        Assert.True(new CoreCompiler().TryCompile(pattern, Profile(mode, flags), ref budget, out var program, out var diagnostic), diagnostic.ToString());
        Assert.Equal(default, diagnostic);
        Assert.NotNull(program);
        Assert.True(budget.ReservedBytes > 0);
        Assert.True(budget.PeakBytes >= budget.ReservedBytes);
        return program;
    }

    public static TheoryData<string> PositiveSyntax => new()
    {
        "", "a", "abc", "😀", "a|b|", "|a", "()", "(?:)", "a||b", "(a(b))",
        "[abc]", "[a-z]", "[^a-z]", "[-a]", "[a-]", @"[\[\]\-]", @"[\d\w\s]", @"[\D\W\S]",
        @"\\\.\^\$\|\?\*\+\(\)\[\]\{\}\-", @"\n\r\t\f\v\0", @"\x00\xFF\u0041\u{1F600}",
        @"\d\D\w\W\s\S", ".^$", "a*", "a+", "a?", "a{0}", "a{2}", "a{2,}", "a{2,4}",
        "a*?", "a+?", "a??", "a{2}?", "a{2,}?", "a{2,4}?", "((a)|(b))*", "(a?)*", "(?:^a$)"
    };
    [Theory, MemberData(nameof(PositiveSyntax))]
    public void EntirePinnedSyntaxCompiles(string pattern) => Compile(pattern);

    public static TheoryData<string, RegexDiagnosticCode> Rejections => new()
    {
        { "(?", RegexDiagnosticCode.InvalidSyntax }, { "(?@a)", RegexDiagnosticCode.InvalidSyntax },
        { "(?=a", RegexDiagnosticCode.InvalidSyntax }, { "(?<>", RegexDiagnosticCode.InvalidSyntax },
        { @"\p", RegexDiagnosticCode.InvalidSyntax }, { @"\p{}", RegexDiagnosticCode.InvalidSyntax },
        { "(*FAIL)", RegexDiagnosticCode.UnsupportedFeature },
        { "(?P<x>a)", RegexDiagnosticCode.UnsupportedFeature }, { "(?#comment)", RegexDiagnosticCode.UnsupportedFeature },
        { "(?R)", RegexDiagnosticCode.UnsupportedFeature }, { @"\k<x>", RegexDiagnosticCode.UnsupportedFeature },
        { @"\k", RegexDiagnosticCode.InvalidSyntax }, { @"\g{}", RegexDiagnosticCode.InvalidSyntax },
        { "a*++", RegexDiagnosticCode.InvalidSyntax },
        { "[", RegexDiagnosticCode.InvalidSyntax }, { "[]", RegexDiagnosticCode.InvalidSyntax },
        { "[^]", RegexDiagnosticCode.InvalidSyntax }, { "[z-a]", RegexDiagnosticCode.InvalidSyntax },
        { @"[a-\d]", RegexDiagnosticCode.InvalidSyntax }, { @"[\w-z]", RegexDiagnosticCode.InvalidSyntax },
        { "[a-b-c]", RegexDiagnosticCode.InvalidSyntax }, { "a{", RegexDiagnosticCode.InvalidSyntax },
        { "a{,2}", RegexDiagnosticCode.InvalidSyntax }, { "a{2,1}", RegexDiagnosticCode.InvalidSyntax },
        { "a{1,2,3}", RegexDiagnosticCode.InvalidSyntax }, { "{a", RegexDiagnosticCode.InvalidSyntax },
        { "a}", RegexDiagnosticCode.InvalidSyntax }, { "a**", RegexDiagnosticCode.InvalidSyntax },
        { "a???", RegexDiagnosticCode.InvalidSyntax }, { "*", RegexDiagnosticCode.InvalidSyntax },
        { "^*", RegexDiagnosticCode.InvalidSyntax }, { ")", RegexDiagnosticCode.InvalidSyntax },
        { "(", RegexDiagnosticCode.InvalidSyntax }, { "\\", RegexDiagnosticCode.InvalidSyntax },
        { @"\x0", RegexDiagnosticCode.InvalidSyntax }, { @"\u00", RegexDiagnosticCode.InvalidSyntax },
        { @"\u{}", RegexDiagnosticCode.InvalidSyntax }, { @"\u{1234567}", RegexDiagnosticCode.InvalidSyntax },
        { @"\q", RegexDiagnosticCode.InvalidSyntax }, { @"\xGG", RegexDiagnosticCode.InvalidSyntax },
        { @"\uD800", RegexDiagnosticCode.InvalidPatternEncoding }, { @"\u{110000}", RegexDiagnosticCode.InvalidPatternEncoding },
        { @"\1", RegexDiagnosticCode.UnsupportedFeature }, { @"\01", RegexDiagnosticCode.UnsupportedFeature },
        { @"\b", RegexDiagnosticCode.UnsupportedFeature }, { @"\B", RegexDiagnosticCode.UnsupportedFeature },
        { @"\p{L}", RegexDiagnosticCode.UnsupportedFeature }, { @"\P{L}", RegexDiagnosticCode.UnsupportedFeature },
        { "(?=a)", RegexDiagnosticCode.UnsupportedFeature }, { "(?!a)", RegexDiagnosticCode.UnsupportedFeature },
        { "(?<=a)", RegexDiagnosticCode.UnsupportedFeature }, { "(?<!a)", RegexDiagnosticCode.UnsupportedFeature },
        { "(?<x>a)", RegexDiagnosticCode.UnsupportedFeature }, { "(?>a)", RegexDiagnosticCode.UnsupportedFeature },
        { "(?(1)a)", RegexDiagnosticCode.UnsupportedFeature }, { "(?|a)", RegexDiagnosticCode.UnsupportedFeature },
        { "(?i:a)", RegexDiagnosticCode.UnsupportedFeature }, { "a*+", RegexDiagnosticCode.UnsupportedFeature },
        { "a{1,2}+", RegexDiagnosticCode.UnsupportedFeature }, { "[a&&b]", RegexDiagnosticCode.UnsupportedFeature },
        { "[a-z--[b]]", RegexDiagnosticCode.UnsupportedFeature }, { "[a-z-[b]]", RegexDiagnosticCode.UnsupportedFeature }
    };
    [Theory, MemberData(nameof(Rejections))]
    public void SyntaxAndExcludedCapabilitiesAreDistinct(string pattern, RegexDiagnosticCode expected)
    {
        var budget = new CompileBudget(new());
        Assert.False(new CoreCompiler().TryCompile(pattern, Profile(), ref budget, out var program, out var diagnostic));
        Assert.Null(program);
        Assert.Equal(expected, diagnostic.Code);
        Assert.InRange(diagnostic.Offset, 0, pattern.Length);
        Assert.InRange(diagnostic.Length, 0, pattern.Length - diagnostic.Offset);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, diagnostic.LocationUnit);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void FullPatternDomainAndRepresentationPrecedeSyntaxAndLength()
    {
        var budget = new CompileBudget(new(maxPatternUnits: 1));
        Assert.False(new CoreCompiler().TryCompile("?\uD800", Profile(), ref budget, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidPatternEncoding, diagnostic.Code);
        Assert.Equal(1, diagnostic.Offset);
        budget = new(new());
        Assert.False(new CoreCompiler().TryCompile([255, 63], Profile(), ref budget, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, diagnostic.Code);
        Assert.Equal(RegexOffsetUnit.Bytes, diagnostic.LocationUnit);
        Assert.Equal(0, budget.WorkConsumed);
        budget = new(new());
        Assert.False(new CoreCompiler().TryCompile("?Ā", Profile(RegularCoreMode.RawBytes), ref budget, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidPatternEncoding, diagnostic.Code);
        Assert.Equal(1, diagnostic.Offset);
    }

    [Fact]
    public void CharAndBytePatternNativeSourceLocationsAndDomains()
    {
        var unit = Compile("\uD800", RegularCoreMode.Utf16CodeUnits);
        Assert.Equal(0xD800, unit.Instructions[1].Operand);
        var scalar = Compile("😀");
        Assert.Equal(0x1F600, scalar.Instructions[1].Operand);
        Assert.Equal(2, scalar.Instructions[1].Source.Length);
        var raw = Compile("ÿ", RegularCoreMode.RawBytes);
        Assert.Equal(255, raw.Instructions[1].Operand);
        var budget = new CompileBudget(new());
        Assert.True(new CoreCompiler().TryCompile([255, 0], Profile(RegularCoreMode.RawBytes), ref budget, out var program, out _));
        Assert.Equal(RegexOffsetUnit.Bytes, program!.PatternUnit);
        Assert.Equal(255, program.Instructions[1].Operand);
        Assert.Equal(0, program.Instructions[2].Operand);
        budget = new(new());
        Assert.False(new CoreCompiler().TryCompile([255, 92, 120, 71, 71], Profile(RegularCoreMode.RawBytes), ref budget, out _, out var diagnostic));
        Assert.Equal(new RegexDiagnostic(RegexDiagnosticCode.InvalidSyntax, 1, 3, RegexOffsetUnit.Bytes), diagnostic);
        budget = new(new());
        Assert.False(new CoreCompiler().TryCompile(@"\u{41}", Profile(RegularCoreMode.Utf16CodeUnits), ref budget, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.UnsupportedFeature, diagnostic.Code);
        budget = new(new());
        Assert.False(new CoreCompiler().TryCompile(@"\u0100", Profile(RegularCoreMode.RawBytes), ref budget, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidPatternEncoding, diagnostic.Code);
    }

    [Fact]
    public void ClassUnionAndAlphabetRelativeComplements()
    {
        foreach (var mode in Enum.GetValues<RegularCoreMode>())
        {
            var program = Compile(@"[zba-c\d]", mode);
            Assert.Equal(new[] { new CharacterRange(48, 57), new(97, 99), new(122, 122) }, program.Classes[0].Ranges.ToArray());
            var all = Compile(@"[\d\D]", mode).Classes[0];
            var max = mode == RegularCoreMode.RawBytes ? 255 : mode == RegularCoreMode.Utf16CodeUnits ? 65535 : 0x10FFFF;
            Assert.Equal(new[] { new CharacterRange(0, max) }, all.Ranges.ToArray());
            Assert.True(Compile("[^a]", mode).Classes[0].Negated);
            foreach (var (pattern, positive) in new[] { (@"\d", "0123456789"), (@"\w", "Az09_"), (@"\s", "\t\n\v\f\r ") })
            {
                var p = Compile(pattern, mode);
                foreach (var ch in positive)
                {
                    Assert.True(ClassContains(p.Classes[0], ch, p.Profile));
                }
            }
        }
        Assert.Single(Compile("[acb]", limits: new(maxClassRanges: 1)).Classes[0].Ranges.ToArray());
        var scalar = Compile(@"[\D]");
        Assert.False(ClassContains(scalar.Classes[0], 0xD800, scalar.Profile));
    }

    public static TheoryData<string, string, int, int> OrderedCases => new()
    {
        { "a|aa", "aa", 0, 1 }, { "aa|a", "aa", 0, 2 }, { "a*", "aaa", 0, 3 }, { "a*?", "aaa", 0, 0 },
        { "a+?", "aaa", 0, 1 }, { "a??", "a", 0, 0 }, { "a{2,3}", "aaa", 0, 3 }, { "a{2,3}?", "aaa", 0, 2 },
        { "a{2,}", "aaaa", 0, 4 }, { "a{2,}?", "aaaa", 0, 2 }, { "a{0}", "a", 0, 0 },
        { "(ab|a)b", "ab", 0, 2 }, { "(?:a|)b", "xb", 1, 1 }, { "^a$", "a", 0, 1 },
        { "a$", "a\n", -1, 0 }, { ".", "\na", 1, 1 }, { "a{0,2}b", "aab", 0, 3 },
        { "(?:)*", "x", 0, 0 }, { "(a?)*b", "b", 0, 1 }, { "(a?)*b", "aab", 0, 3 }
    };
    [Theory, MemberData(nameof(OrderedCases))]
    public void IndependentIrWalkChecksOrderedLowering(string pattern, string input, int index, int length)
    {
        var result = Walk(Compile(pattern), input);
        Assert.Equal(index, result?.Index ?? -1);
        Assert.Equal(length, result?.Length ?? 0);
    }

    [Fact]
    public void RepeatClearsEveryNestedCaptureAndPreservesFinalParticipation()
    {
        var program = Compile("((a)|(b))+c");
        var result = Walk(program, "abc")!;
        Assert.Equal(4, program.GroupCount);
        Assert.Equal(new RegexCapture(true, 1, 1), result.Groups[1]);
        Assert.Equal(default, result.Groups[2]);
        Assert.Equal(new RegexCapture(true, 1, 1), result.Groups[3]);
        Assert.Contains(program.Instructions.ToArray(), i => i.Opcode == CoreOpcode.ClearCaptures && i.Operand == 1 && i.Count == 3);
        var empty = Walk(Compile("(a?)"), "")!;
        Assert.Equal(new RegexCapture(true, 0, 0), empty.Groups[1]);
        var absent = Walk(Compile("(a)?"), "")!;
        Assert.Equal(default, absent.Groups[1]);
    }

    [Fact]
    public void PrologueEpilogueAndImmutableCopiesAreAlwaysAdmitted()
    {
        foreach (var pattern in new[] { "", "a", "a|", "(a)*", "(a){2,4}?", "[a-z]", "(?:)*", "a{0}" })
        {
            var p = Compile(pattern);
            Assert.Equal(CoreOpcode.CaptureStart, p.Instructions[0].Opcode);
            Assert.Equal(CoreOpcode.CaptureEnd, p.Instructions[^2].Opcode);
            Assert.Equal(CoreOpcode.Match, p.Instructions[^1].Opcode);
            Assert.Equal(p.Instructions.Length - 1, p.Instructions[^2].X);
            foreach (var instruction in p.Instructions[..^1])
            {
                Assert.NotEqual(0, instruction.X);
                if (instruction.Opcode == CoreOpcode.Split)
                { Assert.NotEqual(0, instruction.Y); Assert.NotEqual(p.Instructions.Length - 1, instruction.Y); }
            }
        }
        var source = "ab".ToCharArray();
        var budget = new CompileBudget(new());
        Assert.True(new CoreCompiler().TryCompile(source, Profile(), ref budget, out var program, out _));
        source[0] = 'z';
        Assert.Equal('a', program!.Instructions[1].Operand);
        Assert.Equal(4, Compile("a{0}", limits: new(maxInstructions: 4)).Instructions.Length);
    }

    [Fact]
    public void DotAllIsNormalizedProfileMetadata()
    {
        Assert.Null(Walk(Compile("."), "\n"));
        Assert.Equal(1, Walk(Compile(".", flags: RegularCoreFlags.DotAll), "\n")!.Length);
        Assert.True(ProfileDescriptor.TryCreate(new RegularCoreProfile(), RegexInputKind.Utf8, out var utf8, out _));
        var budget = new CompileBudget(new());
        Assert.True(new CoreCompiler().TryCompile("😀", utf8!, ref budget, out var p, out _));
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, p!.PatternUnit);
        Assert.Equal(RegexInputKind.Utf8, p.Profile.InputKind);
    }


    [Fact]
    public void GeneratedCommonSubsetRangesAgreeWithDevelopmentBclComparator()
    {
        var random = new Random(0x434F5245);
        string Make(int depth)
        {
            if (depth == 0)
            {
                return new[] { "a", "b", "[ab]", "", "." }[random.Next(5)];
            }

            return random.Next(5) switch
            {
                0 => "(?:" + Make(depth - 1) + "|" + Make(depth - 1) + ")",
                1 => Make(depth - 1) + Make(depth - 1),
                2 => "(?:" + Make(depth - 1) + "){0,2}",
                3 => "(?:" + Make(depth - 1) + "){1,2}?",
                _ => Make(0)
            };
        }
        for (var sample = 0; sample < 600; sample++)
        {
            var pattern = Make(3);
            string input = new([.. Enumerable.Range(0, random.Next(9)).Select(_ => "abx"[random.Next(3)])]);
            var actual = Walk(Compile(pattern), input);
            var expected = System.Text.RegularExpressions.Regex.Match(input, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            Assert.Equal(expected.Success ? expected.Index : -1, actual?.Index ?? -1);
            Assert.Equal(expected.Success ? expected.Length : 0, actual?.Length ?? 0);
        }
    }

    private static bool ClassContains(CharacterClass @class, int value, ProfileDescriptor profile)
    {
        if (!profile.Contains(value))
        {
            return false;
        }

        var found = false;
        foreach (var r in @class.Ranges)
        {
            found |= value >= r.First && value <= r.Last;
        }

        return found != @class.Negated;
    }

    private sealed record Result(int Index, int Length, RegexCapture[] Groups);
    private sealed record State(int Pc, int Position, int[] Registers, HashSet<int> Epsilon);
    // Nonshipping independently authored DFS oracle for finite test subjects, intentionally allocating.
    // Per-path epsilon cycle detection gives finite walks; it is not a production executor or resource model.
    private static Result? Walk(SemanticProgram program, string input)
    {
        var code = program.Instructions.ToArray();
        for (var candidate = 0; candidate <= input.Length; candidate++)
        {
            var stack = new Stack<State>();
            stack.Push(new(0, candidate, [.. Enumerable.Repeat(-1, 2 * program.GroupCount)], []));
            var steps = 0;
            while (stack.TryPop(out var state))
            {
                Assert.True(++steps < 100_000, "test oracle bound");
                var i = code[state.Pc];
                var position = state.Position;
                var regs = state.Registers;
                if (!state.Epsilon.Add(state.Pc))
                {
                    continue;
                }

                void Next(int pc, int p, bool consumed = false) => stack.Push(new(pc, p, (int[])regs.Clone(), consumed ? [] : new(state.Epsilon)));
                switch (i.Opcode)
                {
                    case CoreOpcode.Match:
                        var captures = new RegexCapture[program.GroupCount];
                        for (var g = 0; g < captures.Length; g++)
                        {
                            if (regs[2 * g] >= 0 && regs[2 * g + 1] >= regs[2 * g])
                            {
                                captures[g] = new(true, regs[2 * g], regs[2 * g + 1] - regs[2 * g]);
                            }
                        }

                        return new(candidate, position - candidate, captures);
                    case CoreOpcode.Split:
                        Next(i.Y, position);
                        Next(i.X, position);
                        break;
                    case CoreOpcode.Jump:
                        Next(i.X, position);
                        break;
                    case CoreOpcode.AssertStart:
                        if (position == 0)
                        {
                            Next(i.X, position);
                        }

                        break;
                    case CoreOpcode.AssertEnd:
                        if (position == input.Length)
                        {
                            Next(i.X, position);
                        }

                        break;
                    case CoreOpcode.CaptureStart:
                        regs[2 * i.Operand] = position;
                        regs[2 * i.Operand + 1] = -1;
                        Next(i.X, position);
                        break;
                    case CoreOpcode.CaptureEnd:
                        regs[2 * i.Operand + 1] = position;
                        Next(i.X, position);
                        break;
                    case CoreOpcode.ClearCaptures:
                        Array.Fill(regs, -1, 2 * i.Operand, 2 * i.Count);
                        Next(i.X, position);
                        break;
                    default:
                        if (position == input.Length)
                        {
                            break;
                        }

                        int value = input[position];
                        var width = 1;
                        if (program.Profile.TextModel == TextModel.UnicodeScalars && char.IsHighSurrogate(input[position]))
                        { value = char.ConvertToUtf32(input[position], input[position + 1]); width = 2; }
                        var accepted = i.Opcode == CoreOpcode.Literal ? value == i.Operand : i.Opcode == CoreOpcode.Class ?
                            ClassContains(program.Classes[i.Operand], value, program.Profile) : value != 10 || program.Profile.Flags.HasFlag(RegularCoreFlags.DotAll);
                        if (accepted)
                        {
                            Next(i.X, position + width, true);
                        }

                        break;
                }
            }
        }
        return null;
    }
}
