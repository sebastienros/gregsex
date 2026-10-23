#nullable enable
using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Flavors.Oniguruma;

// RegexCompileLimits' work and memory ceilings for one compilation, shared by the parser and every compiler pass.
// Work is pattern units, class intervals built and instruction slots emitted; memory is the parse tree, its class
// intervals and the live pass's code.
internal sealed class OnigBudget(RegexCompileLimits limits)
{
    // A parse node with its child list, as retained while compiling.
    internal const int NodeBytes = 128;

    private CompileBudget _budget = new(limits);

    internal RegexCompileLimits Limits { get; } = limits;

    internal void Charge(long units, int offset = 0, int length = 0)
    {
        if (!_budget.TryCharge(units, new SourceRange(offset, length, RegexOffsetUnit.Utf16CodeUnits), out var d))
        {
            throw new OnigSyntaxException(d.Code, offset, length, "compile work limit exceeded");
        }
    }

    internal void Reserve(long bytes, int offset = 0, int length = 0)
    {
        if (!_budget.TryReserveBytes(bytes, new SourceRange(offset, length, RegexOffsetUnit.Utf16CodeUnits), out var d))
        {
            throw new OnigSyntaxException(d.Code, offset, length, "compile memory limit exceeded");
        }
    }

    internal void Release(long bytes) => _budget.ReleaseBytes(bytes);
}
