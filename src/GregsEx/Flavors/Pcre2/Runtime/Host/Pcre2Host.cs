#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

internal sealed partial class Pcre2Host : IPcre2Host
{
    public Pcre2SubstituteResult Substitute(IPcre2Executor executor, ReadOnlySpan<char> input, int startAt,
        ReadOnlySpan<char> replacement, Span<char> destination, Pcre2SubstituteFlags flags,
        Pcre2MatchOptions options, Pcre2CharCallbacks? callbacks, Pcre2HostLimits limits,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs, Pcre2MatchResult precomputedResult,
        Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs) =>
        Substitute<char, Pcre2CharCallbacks, Pcre2HostChars>(executor, input, startAt, replacement, destination,
            flags, options, callbacks, limits, precomputedPairs, precomputedResult, capacity, ref state, callbackPairs, null);
    public Pcre2SubstituteResult Substitute(IPcre2Executor executor, ReadOnlySpan<byte> input, int startAt,
        ReadOnlySpan<byte> replacement, Span<byte> destination, Pcre2SubstituteFlags flags,
        Pcre2MatchOptions options, Pcre2ByteCallbacks? callbacks, Pcre2HostLimits limits,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs, Pcre2MatchResult precomputedResult,
        Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs) =>
        Substitute<byte, Pcre2ByteCallbacks, Pcre2HostBytes>(executor, input, startAt, replacement, destination,
            flags, options, callbacks, limits, precomputedPairs, precomputedResult, capacity, ref state, callbackPairs, null);

    // Absent native match data requests automatic full capacity. Explicit data
    // retains its supplied capacity even when this operation performs the match.
    // MATCHED data continues to derive capacity from its precomputed vector.
    internal Pcre2SubstituteResult Substitute(IPcre2Executor executor, ReadOnlySpan<char> input, int startAt,
        ReadOnlySpan<char> replacement, Span<char> destination, Pcre2SubstituteFlags flags,
        Pcre2MatchOptions options, Pcre2CharCallbacks? callbacks, Pcre2HostLimits limits,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs, Pcre2MatchResult precomputedResult,
        Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs, int? pairCapacity) =>
        Substitute<char, Pcre2CharCallbacks, Pcre2HostChars>(executor, input, startAt, replacement, destination,
            flags, options, callbacks, limits, precomputedPairs, precomputedResult, capacity, ref state, callbackPairs, pairCapacity);
    internal Pcre2SubstituteResult Substitute(IPcre2Executor executor, ReadOnlySpan<byte> input, int startAt,
        ReadOnlySpan<byte> replacement, Span<byte> destination, Pcre2SubstituteFlags flags,
        Pcre2MatchOptions options, Pcre2ByteCallbacks? callbacks, Pcre2HostLimits limits,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs, Pcre2MatchResult precomputedResult,
        Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs, int? pairCapacity) =>
        Substitute<byte, Pcre2ByteCallbacks, Pcre2HostBytes>(executor, input, startAt, replacement, destination,
            flags, options, callbacks, limits, precomputedPairs, precomputedResult, capacity, ref state, callbackPairs, pairCapacity);

    private static Pcre2SubstituteResult Substitute<T, C, U>(IPcre2Executor executor, ReadOnlySpan<T> input,
        int startAt, ReadOnlySpan<T> replacement, Span<T> destination, Pcre2SubstituteFlags flags,
        Pcre2MatchOptions options, C? callbacks, Pcre2HostLimits limits,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs, Pcre2MatchResult precomputedResult,
        Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs, int? pairCapacity)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(limits);
        if (pairCapacity is int supplied)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(supplied, nameof(pairCapacity));
        }

        var program = executor.Program;
        var profile = program.Profile;
        if (profile.Width != default(U).Width)
        {
            throw new ArgumentException("Native host width mismatch.", nameof(input));
        }

        if (input.Overlaps(destination) || replacement.Overlaps(destination))
        {
            throw new ArgumentException("Substitute input/replacement and output must be disjoint.", nameof(destination));
        }

        if (callbackPairs.Length < program.GroupCount)
        {
            throw new ArgumentException("Full callback staging is required.", nameof(callbackPairs));
        }

        if (!state.TryCharge(0, Math.Max(0, startAt)))
        {
            return Failed(ref state);
        }

        if ((options.Flags & (Pcre2MatchFlags.PartialHard | Pcre2MatchFlags.PartialSoft)) != 0 || ((uint)flags & ~0x00039F00u) != 0)
        {
            return Error(Pcre2ErrorCode.Badoption);
        }

        if (startAt < 0 || startAt > input.Length)
        {
            return Error(Pcre2ErrorCode.Badoffset);
        }

        var logical = options.Limits ?? new Pcre2MatchLimits();
        var machine = new Pcre2VmState(capacity, logical, input.Length, program.PatternLimits, startAt);
        if (!Pcre2KernelOperation.PollEntry(program, ref machine, ref state))
        {
            return new(machine.SourceReturnCode != 0 ? machine.SourceReturnCode : (int)Pcre2ErrorCode.Matchlimit, -1, 0, state.Failure);
        }

        var matched = (flags & Pcre2SubstituteFlags.Matched) != 0;
        if (matched && precomputedPairs.IsEmpty)
        {
            return Error(Pcre2ErrorCode.Null);
        }

        var pairCount = matched ? precomputedPairs.Length : pairCapacity ?? program.GroupCount;
        var memory = checked(256L + pairCount * 16L);
        if (memory > limits.MaxMemoryBytes)
        {
            return HostMemory(memory, startAt, ref state);
        }

        var pairs = new Pcre2OffsetPair[pairCount];
        if (matched)
        {
            precomputedPairs.CopyTo(pairs);
        }
        // Replacement validity is checked even if no subject match exists.
        if (profile.Utf && (options.Flags & Pcre2MatchFlags.NoUtfCheck) == 0)
        {
            var valid = default(U).Validate(replacement, profile, default, ref state);
            if (!state.TryCharge(0, startAt))
            {
                return Failed(ref state);
            }

            if (!valid.Valid)
            {
                return new(valid.NativeError, -1, 0, valid.Failure);
            }
        }
        var output = new Pcre2HostOutput<T>(destination, flags, limits);
        var only = (flags & Pcre2SubstituteFlags.ReplacementOnly) != 0;
        if (!only && !output.Copy(input[..startAt], ref state))
        {
            return OutputError(ref output, ref state);
        }

        int subs = 0, previousStart = -1, previousEnd = -1, previousOffset = -1;
        var calloutBudget = new Pcre2HostCallbackBudget(logical.MaxCallouts);
        callbacks = default(U).Wrap(callbacks, calloutBudget);
        var quoted = false;
        Pcre2MatchFlags retry = default;
        while (true)
        {
            if (!state.TryCharge(1, startAt))
            {
                return Failed(ref state);
            }

            Pcre2MatchResult result;
            if (matched)
            { result = precomputedResult; matched = false; }
            else
            {
                result = default(U).Match(executor, input, startAt, options with { Flags = options.Flags | retry }, callbacks,
                capacity, ref state, callbackPairs, pairs);
            }

            if (calloutBudget.Denied)
            { state.Failure = new(RegexExecutionFailureCode.WorkLimitExceeded, startAt, state.Budget.WorkConsumed); return Failed(ref state); }
            if (result.Status != Pcre2MatchStatus.Match)
            {
                if (result.Status != Pcre2MatchStatus.NoMatch)
                {
                    return new(result.NativeReturnCode, -1, 0, result.Failure);
                }

                if (retry == 0 || startAt == input.Length)
                {
                    break;
                }

                var next = default(U).Advance(input, startAt, profile, ref state);
                if (!state.TryCharge(0, startAt))
                {
                    return Failed(ref state);
                }

                if (next <= startAt || next > input.Length)
                {
                    return Error(Pcre2ErrorCode.Badoffset);
                }

                if (!only && !output.Copy(input.Slice(startAt, next - startAt), ref state))
                {
                    return OutputError(ref output, ref state);
                }

                startAt = next;
                retry = default;
                continue;
            }
            // Native rc is capture_top, not match-data vector capacity. The
            // frozen managed callback view exposes the complete supplied vector;
            // undefined upper slots are explicitly unset, including MATCHED data.
            var defined = Math.Min(pairs.Length, Math.Min(program.GroupCount,
                result.NativeReturnCode == 0 ? program.GroupCount : result.NativeReturnCode));
            if (!state.TryCharge(pairs.Length - defined, startAt))
            {
                return Failed(ref state);
            }

            pairs.AsSpan(defined).Clear();
            var pair = pairs[0];
            if (!pair.Participated || pair.End < pair.Start || pair.Start < startAt || pair.End > input.Length)
            {
                return Error(Pcre2ErrorCode.Badsubspattern);
            }

            if (pair.Start == previousStart && pair.End == previousEnd)
            {
                if (pair.Start == pair.End && previousOffset != startAt)
                { retry = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.NotemptyAtstart; previousOffset = startAt; continue; }
                return Error(Pcre2ErrorCode.InternalDupmatch);
            }
            if (subs == limits.MaxResults)
            {
                return HostMemory(subs + 1L, startAt, ref state);
            }

            subs++;
            if (!only && !output.Copy(input.Slice(startAt, pair.Start - startAt), ref state))
            {
                return OutputError(ref output, ref state);
            }

            var insert = output.Offset;
            var beforeExtra = output.Extra;
            var rc = Expand<T, C, U>(program, input, replacement, pairs, result.MarkIndex, flags, callbacks,
                ref calloutBudget.Count, logical.MaxCallouts, ref output, ref state, out var errorOffset, ref quoted);
            if (rc != 0)
            {
                return new(rc, errorOffset, 0, state.Failure);
            }

            if (default(U).HasSubstitute(callbacks))
            {
                if (!output.Overflowed)
                {
                    if (!BeginCallback(ref calloutBudget.Count, logical.MaxCallouts, startAt, ref state))
                    {
                        return Failed(ref state);
                    }

                    var returned = default(U).Substitute(callbacks!, input, output.Buffer[..output.Offset], pairs, new(subs, insert, output.Offset));
                    if (!state.TryCharge(0, startAt))
                    {
                        return Failed(ref state);
                    }

                    if (returned != 0)
                    {
                        output.Offset = insert;
                        if (!only && !output.Copy(input.Slice(pair.Start, pair.End - pair.Start), ref state))
                        {
                            return OutputError(ref output, ref state);
                        }

                        if (returned < 0)
                        {
                            flags &= ~Pcre2SubstituteFlags.Global;
                        }
                    }
                }
                else
                {
                    var replacementSize = output.Offset - insert + output.Extra - beforeExtra;
                    long originalSize = pair.End - pair.Start;
                    if (originalSize > replacementSize && !output.Inflate(originalSize - replacementSize, ref state))
                    {
                        return OutputError(ref output, ref state);
                    }
                }
            }
            previousStart = pair.Start;
            previousEnd = pair.End;
            previousOffset = startAt;
            retry = pair.Start == pair.End && pair.Start == startAt ? Pcre2MatchFlags.Anchored | Pcre2MatchFlags.NotemptyAtstart : 0;
            startAt = pair.End;
            if ((flags & Pcre2SubstituteFlags.Global) == 0)
            {
                break;
            }
        }
        if (!only && !output.Copy(input[startAt..], ref state))
        {
            return OutputError(ref output, ref state);
        }

        Span<T> terminator = stackalloc T[1];
        terminator.Clear();
        if (!output.Copy(terminator, ref state))
        {
            return OutputError(ref output, ref state);
        }

        return output.Overflowed ? new((int)Pcre2ErrorCode.Nomemory, -1, output.Total) : new(subs, output.Offset - 1, output.Offset);
    }
    private static bool BeginCallback(ref int count, int maximum, int offset, ref RunState state)
    {
        if (!state.TryCharge(1, offset))
        {
            return false;
        }

        if (count >= maximum)
        { state.Failure = new(RegexExecutionFailureCode.WorkLimitExceeded, offset, state.Budget.WorkConsumed); return false; }
        count++;
        return true;
    }
    private static Pcre2SubstituteResult HostMemory(long required, int offset, ref RunState state)
    { state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, offset, state.Budget.WorkConsumed, required); return new((int)Pcre2ErrorCode.Heaplimit, -1, 0, state.Failure); }
    private static Pcre2SubstituteResult Error(Pcre2ErrorCode code) => new((int)code, -1, 0);
    private static Pcre2SubstituteResult Failed(ref RunState state) => new((int)Pcre2ErrorCode.Matchlimit, -1, 0, state.Failure);
    private static Pcre2SubstituteResult OutputError<T>(ref Pcre2HostOutput<T> output, ref RunState state) where T : unmanaged =>
        new(output.Error != 0 ? output.Error : (int)Pcre2ErrorCode.Matchlimit, -1, 0, state.Failure);
}
