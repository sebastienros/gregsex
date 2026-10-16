using GregsEx.Contracts;
using GregsEx.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

public sealed class AdmissionOracleTests
{
    private static readonly string[] Escapes = ["a", "q", "c", "c0", "cA", "c_", "0", "01", "377", "400", "8", "12", "123", "x00", "x0G", "u0041", "uD800", "u{41}", "u{}", "u{110000}", "k<x>", "b", "B", "d", "w", "s", "-", "/"];
    private static readonly string[] ClassTokens = ["a", "b", "-", "[a]", "[^b]", @"\d", @"\w", @"\b", @"\c_", @"\1", @"\q{a}", @"\q{ab}", @"\q{}", "&&", "--", "!", "!!", "^", "^^", "/", @"\-", @"\&", "a-z"];
    private static readonly string[] Prefixes = ["", "?:", "?=", "?!", "?<=", "?<!", "?<x>", "?i:", "?-i:", "?i-:", "?i-i:", "?ii:", "?-:", "?u:", "?s:", "?im-s:"];
    private static readonly string[] Operands = ["a", "b", "[a-z]", "[^a]", @"\q{ab|a}", @"\q{}", @"\d", "a-z"];
    private static string Pattern(int index)
    {
        var state = unchecked(0xEC2025u ^ ((uint)(index + 1) * 0x9E3779B9u));
        int Pick(int n)
        { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (int)(state % (uint)n); }
        switch (index % 6)
        {
            case 0:
                const string alphabet = "ab^$.*+?()[]{}|\\0128-=!:<>&q";
                var length = Pick(12);
                var chars = new char[length];
                for (var i = 0; i < length; i++)
                {
                    chars[i] = alphabet[Pick(alphabet.Length)];
                }

                return new(chars);
            case 1:
                return "\\" + Escapes[Pick(Escapes.Length)] + new[] { "", "(a)", "[a]" }[Pick(3)];
            case 2:
                var count = 1 + Pick(5);
                var text = "[";
                for (var i = 0; i < count; i++)
                {
                    text += ClassTokens[Pick(ClassTokens.Length)];
                }

                return text + "]";
            case 3:
                return "a{" + Pick(501) + "," + Pick(501) + "}" + new[] { "", "?", "+" }[Pick(3)];
            case 4:
                return "(" + Prefixes[Pick(Prefixes.Length)] + new[] { "a", "a|b", "(?<x>a)|(?<x>b)", "(?<x>a)(?<x>b)" }[Pick(4)] + ")" + new[] { "", "*", "?" }[Pick(3)];
            default:
                return "[" + Operands[Pick(Operands.Length)] + new[] { "", "&&", "--" }[Pick(3)] + Operands[Pick(Operands.Length)] + "]";
        }
    }
    [Theory]
    [InlineData("", 0)]
    [InlineData("u", 1)]
    [InlineData("v", 2)]
    public void PinnedGeneratedAdmissionWitnesses(string flags, int mode)
    {
        var errors = new List<string>();
        for (var i = 0; i < AdmissionExpectations.Count; i++)
        {
            var pattern = Pattern(i);
            var expected = AdmissionExpectations.Bits[mode * AdmissionExpectations.Count + i] == '1';
            var budget = new CompileBudget(new());
            var actual = new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile(flags), ContractUnicode.Instance, ref budget, out var program, out var diagnostic);
            if (expected != actual && errors.Count < 20)
            {
                errors.Add($"case {i}: {pattern} / {flags} expected {expected}, actual {actual}: {diagnostic}");
            }

            if (!actual)
            {
                Assert.Equal(RegexDiagnosticCode.InvalidSyntax, diagnostic.Diagnostic.Code);
            }

            Assert.Equal(program?.RetainedBytes ?? 0, budget.ReservedBytes);
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
