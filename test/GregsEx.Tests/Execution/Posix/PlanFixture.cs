using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using GregsEx.Execution.Posix;
using Xunit;

namespace GregsEx.Tests.Execution.Posix;

// Nonshipping hand-plan factory. Source positions are assigned independently of
// any production grammar, with strict parent containment and lexical ordering.
internal static class PlanFixture
{
    internal sealed record Tree(PosixNodeKind Kind, Tree? X = null, Tree? Y = null, int Value = 0, int Min = 0, int Max = 0);
    internal static Tree L(int b) => new(PosixNodeKind.Literal, Value: b);
    internal static Tree E() => new(PosixNodeKind.Empty);
    internal static Tree S(Tree x, Tree y) => new(PosixNodeKind.Sequence, x, y);
    internal static Tree A(Tree x, Tree y) => new(PosixNodeKind.Alternative, x, y);
    internal static Tree R(Tree x, int min = 0, int max = -1) => new(PosixNodeKind.Repeat, x, Min: min, Max: max);
    internal static Tree C(Tree x, int g) => new(PosixNodeKind.Capture, x, Value: g);
    internal static Tree B(int g) => new(PosixNodeKind.Backreference, Value: g);
    internal static Tree Anchor(bool end) => new(end ? PosixNodeKind.End : PosixNodeKind.Start);
    internal static IPosixExecutor Executor(Tree tree, PosixCompileFlags flags = 0, int[]? parents = null,
        PosixDialect dialect = PosixDialect.Extended, PosixByteSet[]? sets = null)
    {
        var nodes = new List<PosixNode>();
        var offset = 0;
        int Emit(Tree t)
        {
            var start = offset++;
            int x = t.X is null ? -1 : Emit(t.X), y = t.Y is null ? -1 : Emit(t.Y);
            offset++;
            nodes.Add(new(t.Kind, x, y, t.Value, t.Min, t.Max, new(start, offset - start, RegexOffsetUnit.Bytes)));
            return nodes.Count - 1;
        }
        Emit(tree);
        var budget = new CompileBudget(new());
        Assert.True(PosixProgram.TryCreate(new(dialect, flags), offset, RegexOffsetUnit.Bytes, nodes.ToArray(), sets ?? [],
            parents ?? [-1], ref budget, out var program, out var d), d.ToString());
        Assert.True(new PosixExecutorFactory().TryCreate(program!, new Locale(), ref budget, out var executor, out d), d.ToString());
        return executor!;
    }
    internal sealed class Locale : IPosixLocaleData
    {
        internal readonly ushort[] Masks = new ushort[256];
        internal readonly byte[] Fold = new byte[256], Ordinal = new byte[256];
        public PosixLocale Identity => PosixLocale.C;
        public ReadOnlySpan<ushort> ClassMasks => Masks;
        public ReadOnlySpan<byte> FoldToLower => Fold;
        public ReadOnlySpan<byte> CollationOrdinals => Ordinal;
        internal Locale()
        {
            for (var i = 0; i < 256; i++)
            {
                var c = (char)i;
                bool a = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z', d = c is >= '0' and <= '9';
                var graph = i is >= 33 and <= 126;
                bool[] classes = [a || d, a, i is 9 or 32, i < 32 || i == 127, d, graph, c is >= 'a' and <= 'z',
                    i is >= 32 and <= 126, graph && !a && !d, i is 9 or 10 or 11 or 12 or 13 or 32,
                    c is >= 'A' and <= 'Z', d || c is >= 'A' and <= 'F' or >= 'a' and <= 'f'];
                for (var k = 0; k < classes.Length; k++)
                {
                    if (classes[k])
                    {
                        Masks[i] |= (ushort)(1 << k);
                    }
                }

                Fold[i] = (byte)(c is >= 'A' and <= 'Z' ? i + 32 : i);
                Ordinal[i] = (byte)i;
            }
        }
        public bool TryGetClass(ReadOnlySpan<byte> name, out PosixCharacterClass value) { value = default; return false; }
        public bool TryGetCollatingElement(ReadOnlySpan<byte> name, out byte value) { value = default; return false; }
    }
    internal sealed class Storage
    {
        internal readonly int[] States, Captures, Scratch;
        internal Storage(IPosixExecutor executor, PosixExecutionCapacity? capacity = null, int spare = 0)
        {
            var c = executor.GetRequiredCapacity(SearchOperation.Captures, capacity ?? PosixExecutionCapacity.Default);
            States = new int[c.StateSlots + spare];
            Captures = new int[c.CaptureSlots + spare];
            Scratch = new int[c.ScratchSlots + spare];
        }
        internal WorkspaceBuffers Buffers => new(States, Captures, Scratch);
    }
    internal static RegexSearchStatus Search(IPosixExecutor executor, ReadOnlySpan<byte> input, RegexCapture[] groups,
        out RegexMatchRange match, out RegexExecutionFailure failure, SearchOperation operation = SearchOperation.Captures,
        int start = 0, PosixExecutionFlags flags = 0, PosixExecutionCapacity? capacity = null, Storage? storage = null,
        RegexMatchLimits? limits = null, RegexMatchOptions options = default)
    {
        var state = new RunState((storage ?? new Storage(executor, capacity)).Buffers, limits ?? new(), options);
        var result = executor.Search(input, start, operation, flags, capacity ?? PosixExecutionCapacity.Default, ref state, groups, out match);
        failure = state.Failure;
        return result;
    }
}
