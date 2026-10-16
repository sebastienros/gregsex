using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Flavors.XmlShared;

// Iterative compile-time interval algebra. Scratch buffers and the final exact
// copy are admitted before allocation; failure releases only this call's scratch.
// Work units: inspected interval/alphabet pairs, sort comparisons, merge/subtract
// steps, case rows and membership/key comparisons, and copied output intervals.
internal sealed class XmlClassAlgebra : IXmlClassAlgebra
{
    public bool TryApply(XmlSetOperation operation, ReadOnlySpan<XmlInterval> left,
        ReadOnlySpan<XmlInterval> right, IXmlUnicodeData data, ref CompileBudget budget,
        SourceRange source, out XmlInterval[]? intervals, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        intervals = null;
        diagnostic = default;
        long temporaryBytes = 0;
        try
        {
            if (!Normalize(left, data.Alphabet, ref budget, source, ref temporaryBytes, out var a, out var na, out diagnostic))
            {
                return false;
            }

            XmlInterval[] b = [];
            var nb = 0;
            if (operation is XmlSetOperation.Union or XmlSetOperation.Subtract &&
                !Normalize(right, data.Alphabet, ref budget, source, ref temporaryBytes, out b, out nb, out diagnostic))
            {
                return false;
            }

            ReadOnlySpan<XmlInterval> x = a.AsSpan(0, na), y = b.AsSpan(0, nb);
            if (operation == XmlSetOperation.Complement)
            { x = data.Alphabet; y = a.AsSpan(0, na); }
            if (operation == XmlSetOperation.CaseVariants)
            {
                if (!ExpandCases(x, data.CaseMappings, ref budget, source, ref temporaryBytes, out var expanded, out var count, out diagnostic))
                {
                    return false;
                }

                return Publish(expanded.AsSpan(0, count), temporaryBytes, ref budget, source, out intervals, out diagnostic);
            }
            // Count before allocation, then repeat the charged streaming operation.
            if (!Combine(operation == XmlSetOperation.Union, x, y, [], false, ref budget, source, out var needed, out diagnostic) ||
                !AllocateIntervals(needed, ref budget, source, ref temporaryBytes, out var output, out diagnostic) ||
                !Combine(operation == XmlSetOperation.Union, x, y, output, true, ref budget, source, out var written, out diagnostic))
            {
                return false;
            }

            return Publish(output.AsSpan(0, written), temporaryBytes, ref budget, source, out intervals, out diagnostic);
        }
        finally { budget.ReleaseBytes(temporaryBytes); }
    }

    private static bool Normalize(ReadOnlySpan<XmlInterval> input, ReadOnlySpan<XmlInterval> alphabet,
        ref CompileBudget budget, SourceRange source, ref long temporaryBytes,
        out XmlInterval[] output, out int count, out RegexDiagnostic diagnostic)
    {
        output = [];
        count = 0;
        diagnostic = default;
        foreach (var range in input)
        {
            if (range.First < 0 || range.Last > 0x10FFFF || range.First > range.Last)
            {
                throw new ArgumentException("Class interval must be an ordered Unicode range.", nameof(input));
            }

            foreach (var legal in alphabet)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                if (range.First <= legal.Last && legal.First <= range.Last)
                {
                    count = checked(count + 1);
                }
            }
        }
        if (!AllocateIntervals(count, ref budget, source, ref temporaryBytes, out output, out diagnostic))
        {
            return false;
        }

        var at = 0;
        foreach (var range in input)
        {
            foreach (var legal in alphabet)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                if (range.First <= legal.Last && legal.First <= range.Last)
                {
                    output[at++] = new(Math.Max(range.First, legal.First), Math.Min(range.Last, legal.Last));
                }
            }
        }

        if (!Sort(output, ref budget, source, out diagnostic))
        {
            return false;
        }

        return MergeInPlace(output, count, ref budget, source, out count, out diagnostic);
    }

    private static bool Combine(bool union, ReadOnlySpan<XmlInterval> a, ReadOnlySpan<XmlInterval> b,
        Span<XmlInterval> output, bool write, ref CompileBudget budget, SourceRange source,
        out int count, out RegexDiagnostic diagnostic)
    {
        count = 0;
        diagnostic = default;
        if (union)
        {
            int i = 0, j = 0;
            XmlInterval pending = default;
            var hasPending = false;
            while (i < a.Length || j < b.Length)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                var next = j == b.Length || i < a.Length && a[i].First <= b[j].First ? a[i++] : b[j++];
                if (hasPending && next.First <= pending.Last + 1)
                {
                    pending = new(pending.First, Math.Max(pending.Last, next.Last));
                }
                else
                {
                    if (hasPending)
                    { if (write) { output[count] = pending; } count++; }
                    pending = next;
                    hasPending = true;
                }
            }
            if (hasPending)
            { if (write) { output[count] = pending; } count++; }
            return true;
        }
        var k = 0;
        foreach (var range in a)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            var cursor = range.First;
            while (k < b.Length && b[k].Last < cursor)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                k++;
            }
            var j = k;
            while (j < b.Length && b[j].First <= range.Last && cursor <= range.Last)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                if (b[j].First > cursor)
                { if (write) { output[count] = new(cursor, b[j].First - 1); } count++; }
                cursor = Math.Max(cursor, b[j].Last + 1);
                j++;
            }
            if (cursor <= range.Last)
            { if (write) { output[count] = new(cursor, range.Last); } count++; }
        }
        return true;
    }

    private static bool ExpandCases(ReadOnlySpan<XmlInterval> input, ReadOnlySpan<XmlCaseMapping> mappings,
        ref CompileBudget budget, SourceRange source, ref long temporaryBytes,
        out XmlInterval[] output, out int count, out RegexDiagnostic diagnostic)
    {
        output = [];
        count = 0;
        diagnostic = default;
        // Sparse rows include every changed mapping AND its identity singleton
        // partners. Missing rows cannot have a variant, independently audited.
        var keyBytes = mappings.IsEmpty ? 0 : checked(64L + 8L * mappings.Length);
        if (!budget.TryReserveBytes(keyBytes, source, out diagnostic))
        {
            return false;
        }

        temporaryBytes += keyBytes;
        var lower = mappings.IsEmpty ? [] : new int[mappings.Length];
        var upper = mappings.IsEmpty ? [] : new int[mappings.Length];
        var keys = 0;
        foreach (var row in mappings)
        {
            if (!budget.TryCharge(1, source, out diagnostic) ||
                !Contains(input, row.Scalar, ref budget, source, out var found, out diagnostic))
            {
                return false;
            }

            if (found)
            { lower[keys] = row.LowerKey; upper[keys++] = row.UpperKey; }
        }
        if (!SortKeys(lower.AsSpan(0, keys), ref budget, source, out diagnostic) ||
            !SortKeys(upper.AsSpan(0, keys), ref budget, source, out diagnostic) ||
            !AllocateIntervals(checked(input.Length + mappings.Length), ref budget, source,
                ref temporaryBytes, out output, out diagnostic))
        {
            return false;
        }

        if (!budget.TryCharge(input.Length, source, out diagnostic))
        {
            return false;
        }

        input.CopyTo(output);
        count = input.Length;
        foreach (var row in mappings)
        {
            if (!budget.TryCharge(1, source, out diagnostic) ||
                !ContainsKey(lower.AsSpan(0, keys), row.LowerKey, ref budget, source, out var low, out diagnostic) ||
                !ContainsKey(upper.AsSpan(0, keys), row.UpperKey, ref budget, source, out var high, out diagnostic))
            {
                return false;
            }

            if (low || high)
            {
                output[count++] = new(row.Scalar, row.Scalar);
            }
        }
        if (!Sort(output.AsSpan(0, count), ref budget, source, out diagnostic))
        {
            return false;
        }

        return MergeInPlace(output, count, ref budget, source, out count, out diagnostic);
    }

    private static bool Contains(ReadOnlySpan<XmlInterval> ranges, int value, ref CompileBudget budget,
        SourceRange source, out bool found, out RegexDiagnostic diagnostic)
    {
        found = false;
        diagnostic = default;
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            var mid = lo + ((hi - lo) >> 1);
            if (value < ranges[mid].First)
            {
                hi = mid - 1;
            }
            else if (value > ranges[mid].Last)
            {
                lo = mid + 1;
            }
            else
            { found = true; break; }
        }
        return true;
    }
    private static bool ContainsKey(ReadOnlySpan<int> keys, int value, ref CompileBudget budget,
        SourceRange source, out bool found, out RegexDiagnostic diagnostic)
    {
        found = false;
        diagnostic = default;
        int lo = 0, hi = keys.Length - 1;
        while (lo <= hi)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            var mid = lo + ((hi - lo) >> 1);
            if (value < keys[mid])
            {
                hi = mid - 1;
            }
            else if (value > keys[mid])
            {
                lo = mid + 1;
            }
            else
            { found = true; break; }
        }
        return true;
    }
    private static bool AllocateIntervals(int length, ref CompileBudget budget, SourceRange source,
        ref long temporaryBytes, out XmlInterval[] output, out RegexDiagnostic diagnostic)
    {
        output = [];
        var bytes = length == 0 ? 0 : checked(32L + 8L * length);
        if (!budget.TryReserveBytes(bytes, source, out diagnostic))
        {
            return false;
        }

        temporaryBytes += bytes;
        output = length == 0 ? [] : new XmlInterval[length];
        return true;
    }
    private static bool Publish(ReadOnlySpan<XmlInterval> result, long temporaryBytes, ref CompileBudget budget,
        SourceRange source, out XmlInterval[]? intervals, out RegexDiagnostic diagnostic)
    {
        intervals = null;
        diagnostic = default;
        var code = result.Length > budget.Limits.MaxClassRanges ? RegexDiagnosticCode.ClassLimitExceeded :
            (result.IsEmpty ? 0 : 32L + 8L * result.Length) > budget.Limits.MaxPlanBytes - (budget.ReservedBytes - temporaryBytes) ?
                RegexDiagnosticCode.PlanLimitExceeded : RegexDiagnosticCode.None;
        if (code != RegexDiagnosticCode.None)
        { diagnostic = new(code, source.Offset, source.Length, source.Unit); return false; }
        var bytes = result.IsEmpty ? 0 : checked(32L + 8L * result.Length);
        if (!budget.TryCharge(result.Length, source, out diagnostic) || !budget.TryReserveBytes(bytes, source, out diagnostic))
        {
            return false;
        }

        intervals = result.IsEmpty ? [] : new XmlInterval[result.Length];
        result.CopyTo(intervals);
        return true;
    }
    private static bool MergeInPlace(Span<XmlInterval> ranges, int length, ref CompileBudget budget,
        SourceRange source, out int count, out RegexDiagnostic diagnostic)
    {
        count = 0;
        diagnostic = default;
        for (var i = 0; i < length; i++)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            var next = ranges[i];
            if (count > 0 && next.First <= ranges[count - 1].Last + 1)
            {
                ranges[count - 1] = new(ranges[count - 1].First, Math.Max(ranges[count - 1].Last, next.Last));
            }
            else
            {
                ranges[count++] = next;
            }
        }
        return true;
    }
    // Heap sort has bounded O(n log n) work and no input-controlled recursion,
    // comparison delegates, hidden allocations, or uncharged framework sorting.
    private static bool Sort(Span<XmlInterval> a, ref CompileBudget budget, SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        for (var root = a.Length / 2 - 1; root >= 0; root--)
        {
            if (!Sift(a, root, a.Length, ref budget, source, out diagnostic))
            {
                return false;
            }
        }

        for (var end = a.Length - 1; end > 0; end--)
        {
            (a[0], a[end]) = (a[end], a[0]);
            if (!Sift(a, 0, end, ref budget, source, out diagnostic))
            {
                return false;
            }
        }
        return true;
    }
    private static bool Sift(Span<XmlInterval> a, int root, int length, ref CompileBudget budget,
        SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        while (root < length / 2)
        {
            var child = root * 2 + 1;
            if (!budget.TryCharge(2, source, out diagnostic))
            {
                return false;
            }

            if (child + 1 < length && Compare(a[child], a[child + 1]) < 0)
            {
                child++;
            }

            if (Compare(a[root], a[child]) >= 0)
            {
                break;
            }

            (a[root], a[child]) = (a[child], a[root]);
            root = child;
        }
        return true;
    }
    private static int Compare(XmlInterval a, XmlInterval b) => a.First != b.First ? a.First.CompareTo(b.First) : a.Last.CompareTo(b.Last);
    private static bool SortKeys(Span<int> a, ref CompileBudget budget, SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        for (var root = a.Length / 2 - 1; root >= 0; root--)
        {
            if (!SiftKeys(a, root, a.Length, ref budget, source, out diagnostic))
            {
                return false;
            }
        }

        for (var end = a.Length - 1; end > 0; end--)
        {
            (a[0], a[end]) = (a[end], a[0]);
            if (!SiftKeys(a, 0, end, ref budget, source, out diagnostic))
            {
                return false;
            }
        }
        return true;
    }
    private static bool SiftKeys(Span<int> a, int root, int length, ref CompileBudget budget,
        SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        while (root < length / 2)
        {
            var child = root * 2 + 1;
            if (!budget.TryCharge(2, source, out diagnostic))
            {
                return false;
            }

            if (child + 1 < length && a[child] < a[child + 1])
            {
                child++;
            }

            if (a[root] >= a[child])
            {
                break;
            }

            (a[root], a[child]) = (a[child], a[root]);
            root = child;
        }
        return true;
    }
}
