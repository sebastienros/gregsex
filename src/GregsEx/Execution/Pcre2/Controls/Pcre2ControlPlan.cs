#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.IR;

namespace GregsEx.Execution.Pcre2.Controls;

// Immutable lexical context for source CLOSE/ASSERT_ACCEPT and THEN. Native
// recursion references do not add structural parents or erase lexical scope.
internal sealed class Pcre2ControlPlan
{
    private readonly int[] parents;
    internal Pcre2Program Program { get; }
    internal long RetainedBytes { get; }
    internal int Parent(int node) => parents[node];

    private Pcre2ControlPlan(Pcre2Program program, int[] parents, long bytes)
    {
        Program = program;
        this.parents = parents;
        RetainedBytes = bytes;
    }

    internal static bool TryCreate(Pcre2Program program, ref CompileBudget budget,
        out Pcre2ControlPlan? plan, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!program.IsExecutable)
        {
            throw new ArgumentException("Control context requires a validated executable plan.", nameof(program));
        }

        plan = null;
        diagnostic = default;
        var source = new SourceRange(0, program.SourceLength, program.SourceUnit);
        var bytes = checked(96L + 4L * program.Nodes.Length);
        if (bytes > budget.Limits.MaxPlanBytes - program.RetainedBytes)
        {
            diagnostic = new(new(RegexDiagnosticCode.PlanLimitExceeded, 0, program.SourceLength, program.SourceUnit), Pcre2DiagnosticKind.CompileLimit);
            return false;
        }
        if (!budget.TryReserveBytes(bytes, source, out var error))
        {
            diagnostic = new(error, Pcre2DiagnosticKind.CompileLimit);
            return false;
        }
        var retained = false;
        try
        {
            var parents = new int[program.Nodes.Length];
            Array.Fill(parents, -1);
            for (var i = 0; i < program.Nodes.Length; i++)
            {
                var node = program.Nodes[i];
                if (!budget.TryCharge(1L + node.Children.Count, source, out error))
                {
                    diagnostic = new(error, Pcre2DiagnosticKind.CompileLimit);
                    return false;
                }
                foreach (var child in program.Children.Slice(node.Children.Offset, node.Children.Count))
                {
                    parents[child] = i;
                }
            }
            plan = new(program, parents, bytes);
            retained = true;
            return true;
        }
        finally
        {
            if (!retained)
            {
                budget.ReleaseBytes(bytes);
            }
        }
    }

    internal bool IsAssertionAccept(int node, ref Pcre2VmState machine, ref RunState state)
    {
        for (var parent = parents[node]; parent >= 0; parent = parents[parent])
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            if (Program.Nodes[parent].Kind is Pcre2NodeKind.Assertion or Pcre2NodeKind.ScanSubstring)
            {
                return true;
            }
        }
        return false;
    }

    internal bool IsWithin(int node, int ancestor, ref Pcre2VmState machine, ref RunState state)
    {
        for (var cursor = node; cursor >= 0; cursor = parents[cursor])
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            if (cursor == ancestor)
            {
                return true;
            }
        }
        return false;
    }
}
