#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Text;

// Contains coordinates only, never a borrowed subject. ScanStart is the UTF
// lookbehind floor; it does not change original beginning/anchor coordinates.
// Consumers OR FragmentFlags with invocation flags, bound all scalar/\C reads
// by VisibleEnd, and prevent reverse reads below ScanStart. NextFragment is used
// after no-match/partial on a nonterminal fragment, never after source abort.
internal readonly record struct Pcre2TextWindow(Pcre2ValidationResult Validation, bool Available,
    int CandidateStart, int ScanStart, int VisibleEnd, Pcre2MatchFlags FragmentFlags);

internal static class Pcre2TextFragments<TUnit, TUnits>
    where TUnit : unmanaged where TUnits : struct, IPcre2Units<TUnit>
{
    internal static Pcre2TextWindow Bind(scoped ReadOnlySpan<TUnit> input, int startAt, int maxLookbehind,
        Pcre2Profile profile, Pcre2MatchFlags flags, Pcre2ValidationPolicy policy, ref RunState state)
    {
        Pcre2TextCore<TUnit, TUnits>.Admit(input.Length, startAt, profile);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLookbehind);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (!state.TryCharge(0, startAt))
        {
            return Failed(in state);
        }

        var invalid = Pcre2TextCore<TUnit, TUnits>.AllowsInvalid(profile);
        if (policy == Pcre2ValidationPolicy.WholeSubjectProjection &&
            (invalid || (flags & Pcre2MatchFlags.NoUtfCheck) != 0 || (profile.Flags & Pcre2CompileFlags.NoUtfCheck) != 0))
        {
            return new(new(false, (int)Pcre2ErrorCode.Badoption), false, startAt, 0, input.Length, default);
        }

        if (!profile.Utf)
        {
            return new(new(true), true, startAt, 0, input.Length, default);
        }

        int candidate = startAt, scan = policy == Pcre2ValidationPolicy.WholeSubjectProjection ? 0 : startAt;
        if (policy == Pcre2ValidationPolicy.NativeWindow)
        {
            if (candidate < input.Length)
            {
                if (!Pcre2TextCore<TUnit, TUnits>.Inspect(input, candidate, ref state, out var unit))
                {
                    return Failed(in state);
                }

                if (default(TUnits).Interior(unit))
                {
                    if (!invalid)
                    {
                        var error = startAt == 0
                            ? Pcre2TextCore<TUnit, TUnits>.EncodingError(Pcre2TextCore<TUnit, TUnits>.IsolatedError, 0, in state)
                            : Pcre2TextCore<TUnit, TUnits>.BoundaryError(startAt, in state);
                        return new(error, false, startAt, scan, input.Length, default);
                    }
                    candidate = SkipInteriors(input, candidate, input.Length, ref state);
                    if (state.Failure.Code != RegexExecutionFailureCode.None)
                    {
                        return Failed(in state);
                    }

                    scan = candidate;
                    maxLookbehind = 0;
                }
            }
            for (var i = 0; i < maxLookbehind && scan > 0; i++)
            {
                scan = Pcre2TextCore<TUnit, TUnits>.PreviousStart(input, scan, ref state);
                if (scan < 0)
                {
                    return Failed(in state);
                }
            }
        }
        var window = Scan(input, candidate, scan, invalid, default, ref state);
        if (!window.Validation.Valid || state.Failure.Code != RegexExecutionFailureCode.None)
        {
            return window;
        }

        if (policy == Pcre2ValidationPolicy.WholeSubjectProjection && startAt < input.Length)
        {
            if (!Pcre2TextCore<TUnit, TUnits>.Inspect(input, startAt, ref state, out var unit))
            {
                return Failed(in state);
            }

            if (default(TUnits).Interior(unit))
            {
                return window with { Validation = Pcre2TextCore<TUnit, TUnits>.BoundaryError(startAt, in state), Available = false };
            }
        }
        return window;
    }

    internal static Pcre2TextWindow Next(scoped ReadOnlySpan<TUnit> input, Pcre2TextWindow previous,
        Pcre2Profile profile, ref RunState state)
    {
        Pcre2TextCore<TUnit, TUnits>.Admit(input.Length, previous.VisibleEnd, profile);
        if (!state.TryCharge(0, previous.VisibleEnd))
        {
            return Failed(in state);
        }

        if (!previous.Validation.Valid || !previous.Available || previous.VisibleEnd == input.Length)
        {
            return previous with { Available = false };
        }

        if (!Pcre2TextCore<TUnit, TUnits>.AllowsInvalid(profile))
        {
            throw new ArgumentException("Fragment continuation requires invalid-UTF matching.", nameof(profile));
        }

        var candidate = previous.VisibleEnd;
        while (candidate < input.Length)
        {
            // Charge every discarded bad leading unit and following interior.
            if (!state.TryCharge(1, candidate))
            {
                return Failed(in state);
            }

            candidate = SkipInteriors(input, candidate + 1, input.Length, ref state);
            if (state.Failure.Code != RegexExecutionFailureCode.None)
            {
                return Failed(in state);
            }

            if (candidate == input.Length)
            {
                break; // No terminal empty fragment retry.
            }

            var window = Scan(input, candidate, candidate, true, Pcre2MatchFlags.Notbol, ref state);
            if (!window.Validation.Valid)
            {
                return window;
            }

            if (window.VisibleEnd > candidate)
            {
                return window;
            }
        }
        return new(new(true), false, candidate, candidate, input.Length, Pcre2MatchFlags.Notbol);
    }

    private static Pcre2TextWindow Scan(scoped ReadOnlySpan<TUnit> input, int candidate, int scan,
        bool invalid, Pcre2MatchFlags fragmentFlags, ref RunState state)
    {
        var offset = scan;
        if (state.PrevalidatedCleanSubject)
        {
            var rem = input.Length - offset;
            if (rem > 0 && !state.TryChargeRun(offset, rem))
            {
                return Failed(in state);
            }

            state.ValidatedCleanSubject = true;
            return new(new(true), true, candidate, scan, input.Length, fragmentFlags);
        }
        while (offset < input.Length)
        {
            var clean = typeof(TUnit) == typeof(char)
                ? System.Runtime.InteropServices.MemoryMarshal.Cast<TUnit, char>(input.Slice(offset)).IndexOfAnyInRange('\uD800', '\uDFFF')
                : typeof(TUnit) == typeof(byte)
                    ? System.Runtime.InteropServices.MemoryMarshal.Cast<TUnit, byte>(input.Slice(offset)).IndexOfAnyInRange((byte)0x80, (byte)0xFF)
                    : 0;
            if (clean < 0)
            {
                clean = input.Length - offset;
            }

            if (clean > 0)
            {
                if (!state.TryChargeRun(offset, clean))
                {
                    return Failed(in state);
                }

                if (offset == scan && clean == input.Length - scan)
                {
                    state.ValidatedCleanSubject = true;
                    return new(new(true), true, candidate, scan, input.Length, fragmentFlags);
                }
                offset += clean;
                if (offset == input.Length)
                {
                    break;
                }
            }
            var error = Pcre2TextCore<TUnit, TUnits>.Decode(input, offset, input.Length, ref state, out _, out var width);
            if (state.Failure.Code != RegexExecutionFailureCode.None)
            {
                return Failed(in state);
            }

            if (error == 0)
            { offset += width; continue; }
            if (!invalid)
            {
                return new(Pcre2TextCore<TUnit, TUnits>.EncodingError(error, offset, in state), false, candidate, scan, input.Length, fragmentFlags);
            }

            if (offset >= candidate)
            {
                return new(new(true), true, candidate, scan, offset, fragmentFlags | Pcre2MatchFlags.Noteol);
            }
            // Bad lookbehind prefix: restore original end, discard the bad unit,
            // move the scan floor forward and continue. Candidate stays fixed.
            if (!state.TryCharge(1, offset))
            {
                return Failed(in state);
            }

            scan = offset = SkipInteriors(input, offset + 1, candidate, ref state);
            if (state.Failure.Code != RegexExecutionFailureCode.None)
            {
                return Failed(in state);
            }
        }
        return new(new(true), true, candidate, scan, input.Length, fragmentFlags);
    }

    private static int SkipInteriors(scoped ReadOnlySpan<TUnit> input, int offset, int end, ref RunState state)
    {
        while (offset < end)
        {
            if (!Pcre2TextCore<TUnit, TUnits>.Inspect(input, offset, ref state, out var unit))
            {
                return offset;
            }

            if (!default(TUnits).Interior(unit))
            {
                break;
            }

            offset++;
        }
        return offset;
    }

    private static Pcre2TextWindow Failed(in RunState state) =>
        new(Pcre2TextCore<TUnit, TUnits>.Resource(in state), false, 0, 0, 0, default);
}
