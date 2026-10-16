using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using GregsEx.IR;

namespace GregsEx.Execution.Posix;

internal sealed class PosixExecutorFactory : IPosixExecutorFactory
{
    public bool TryCreate(PosixProgram program, IPosixLocaleData locale, ref CompileBudget budget,
        out IPosixExecutor? executor, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(locale);
        executor = null;
        diagnostic = default;
        var source = new SourceRange(0, program.PatternLength, program.PatternUnit);
        if (locale.Identity.ProviderId != program.Profile.Locale.ProviderId ||
            locale.Identity.DataDigest != program.Profile.Locale.DataDigest ||
            locale.ClassMasks.Length != 256 || locale.FoldToLower.Length != 256 || locale.CollationOrdinals.Length != 256)
        {
            return Invalid(source, out diagnostic);
        }
        // Verify the closed provider's actual data, rather than trusting its descriptor.
        for (var b = 0; b < 256; b++)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            bool upper = b is >= 65 and <= 90, lower = b is >= 97 and <= 122, digit = b is >= 48 and <= 57;
            bool alpha = upper || lower, graph = b is >= 33 and <= 126;
            ushort mask = 0;
            ReadOnlySpan<bool> memberships = [alpha || digit, alpha, b is 9 or 32, b < 32 || b == 127,
                digit, graph, lower, b is >= 32 and <= 126, graph && !alpha && !digit,
                b is 9 or 10 or 11 or 12 or 13 or 32, upper, digit || b is >= 65 and <= 70 or >= 97 and <= 102];
            for (var c = 0; c < memberships.Length; c++)
            {
                if (memberships[c])
                {
                    mask |= (ushort)(1 << c);
                }
            }

            if (locale.ClassMasks[b] != mask || locale.CollationOrdinals[b] != b ||
                locale.FoldToLower[b] != (upper ? b + 32 : b))
            {
                return Invalid(source, out diagnostic);
            }
        }
        var bytes = 160L + 4L * program.Nodes.Length;
        if (bytes > budget.Limits.MaxPlanBytes - program.RetainedBytes)
        { diagnostic = new(RegexDiagnosticCode.PlanLimitExceeded, source.Offset, source.Length, source.Unit); return false; }
        if (!budget.TryReserveBytes(bytes, source, out diagnostic))
        {
            return false;
        }

        var scratch = 32L + 4L * program.Nodes.Length;
        if (!budget.TryReserveBytes(scratch, source, out diagnostic))
        {
            return false;
        }

        try
        {
            var order = new int[program.Nodes.Length];
            var stack = new int[program.Nodes.Length];
            int pending = 1, next = 0;
            stack[0] = program.Root;
            while (pending > 0)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                var node = stack[--pending];
                order[next++] = node;
                var n = program.Nodes[node];
                if (n.Y >= 0)
                {
                    stack[pending++] = n.Y;
                }

                if (n.X >= 0)
                {
                    stack[pending++] = n.X;
                }
            }
            executor = new PosixExecutor(program, order);
            return true;
        }
        finally { budget.ReleaseBytes(scratch); }
    }
    private static bool Invalid(SourceRange source, out RegexDiagnostic diagnostic)
    { diagnostic = new(RegexDiagnosticCode.InvalidProgram, source.Offset, source.Length, source.Unit); return false; }
}
