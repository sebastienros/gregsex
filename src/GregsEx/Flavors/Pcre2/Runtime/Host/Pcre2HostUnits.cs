using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Text;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

internal interface IPcre2HostUnits<T, TCallbacks> where T : unmanaged where TCallbacks : class
{
    static abstract Pcre2CodeUnitWidth Width { get; }
    static abstract int Unit(T value);
    static abstract T From(int value);
    static abstract Pcre2ReadResult Read(scoped ReadOnlySpan<T> input, int offset, Pcre2Profile profile, ref RunState state);
    static abstract Pcre2ValidationResult Validate(ReadOnlySpan<T> input, Pcre2Profile profile, Pcre2MatchFlags flags, ref RunState state);
    static abstract int Advance(ReadOnlySpan<T> input, int offset, Pcre2Profile profile, ref RunState state);
    static abstract Pcre2MatchResult Match(IPcre2Executor executor, ReadOnlySpan<T> input, int start,
        Pcre2MatchOptions options, TCallbacks? callbacks, Pcre2ExecutionCapacity capacity, ref RunState state,
        Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs);
    static abstract ReadOnlySpan<T> Mark(Pcre2Program program, int index);
    static abstract TCallbacks? Wrap(TCallbacks? callbacks, Pcre2HostCallbackBudget budget);
    static abstract bool HasCase(TCallbacks? callbacks);
    static abstract bool HasSubstitute(TCallbacks? callbacks);
    static abstract int Case(TCallbacks callbacks, ReadOnlySpan<T> input, Span<T> output, Pcre2SubstituteCase mode);
    static abstract int Substitute(TCallbacks callbacks, ReadOnlySpan<T> input, ReadOnlySpan<T> output,
        ReadOnlySpan<Pcre2OffsetPair> pairs, Pcre2SubstituteCalloutInfo info);
}
internal readonly struct Pcre2HostChars : IPcre2HostUnits<char, Pcre2CharCallbacks>
{
    public static Pcre2CodeUnitWidth Width => Pcre2CodeUnitWidth.Sixteen;
    public static int Unit(char value) => value;
    public static char From(int value) => (char)value;
    public static Pcre2ReadResult Read(scoped ReadOnlySpan<char> input, int offset, Pcre2Profile profile, ref RunState state) => Pcre2CharTextReader.Read(input, offset, input.Length, false, profile, ref state);
    public static Pcre2ValidationResult Validate(ReadOnlySpan<char> input, Pcre2Profile profile, Pcre2MatchFlags flags, ref RunState state) => Pcre2ReplacementValidation.Validate<char, Pcre2CharUnits>(input, ref state);
    public static int Advance(ReadOnlySpan<char> input, int offset, Pcre2Profile profile, ref RunState state) => Pcre2CharTextReader.AdvanceAfterEmpty(input, offset, profile, ref state);
    public static Pcre2MatchResult Match(IPcre2Executor executor, ReadOnlySpan<char> input, int start, Pcre2MatchOptions options, Pcre2CharCallbacks? callbacks, Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs) => executor.Match(input, start, options, callbacks, capacity, Pcre2ValidationPolicy.NativeWindow, ref state, callbackPairs, pairs);
    public static ReadOnlySpan<char> Mark(Pcre2Program program, int index) => index < 0 ? default : program.NativeChars(program.Marks[index]);
    public static Pcre2CharCallbacks? Wrap(Pcre2CharCallbacks? callbacks, Pcre2HostCallbackBudget budget) => budget.Wrap(callbacks);
    public static bool HasCase(Pcre2CharCallbacks? callbacks) => callbacks?.Case is not null;
    public static bool HasSubstitute(Pcre2CharCallbacks? callbacks) => callbacks?.Substitute is not null;
    public static int Case(Pcre2CharCallbacks callbacks, ReadOnlySpan<char> input, Span<char> output, Pcre2SubstituteCase mode) => callbacks.Case!(input, output, mode, callbacks.State);
    public static int Substitute(Pcre2CharCallbacks callbacks, ReadOnlySpan<char> input, ReadOnlySpan<char> output, ReadOnlySpan<Pcre2OffsetPair> pairs, Pcre2SubstituteCalloutInfo info)
    { var context = new Pcre2CharSubstituteContext(input, output, pairs, info); return callbacks.Substitute!(in context, callbacks.State); }
}
internal readonly struct Pcre2HostBytes : IPcre2HostUnits<byte, Pcre2ByteCallbacks>
{
    public static Pcre2CodeUnitWidth Width => Pcre2CodeUnitWidth.Eight;
    public static int Unit(byte value) => value;
    public static byte From(int value) => (byte)value;
    public static Pcre2ReadResult Read(scoped ReadOnlySpan<byte> input, int offset, Pcre2Profile profile, ref RunState state) => Pcre2ByteTextReader.Read(input, offset, input.Length, false, profile, ref state);
    public static Pcre2ValidationResult Validate(ReadOnlySpan<byte> input, Pcre2Profile profile, Pcre2MatchFlags flags, ref RunState state) => Pcre2ReplacementValidation.Validate<byte, Pcre2ByteUnits>(input, ref state);
    public static int Advance(ReadOnlySpan<byte> input, int offset, Pcre2Profile profile, ref RunState state) => Pcre2ByteTextReader.AdvanceAfterEmpty(input, offset, profile, ref state);
    public static Pcre2MatchResult Match(IPcre2Executor executor, ReadOnlySpan<byte> input, int start, Pcre2MatchOptions options, Pcre2ByteCallbacks? callbacks, Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs) => executor.Match(input, start, options, callbacks, capacity, Pcre2ValidationPolicy.NativeWindow, ref state, callbackPairs, pairs);
    public static ReadOnlySpan<byte> Mark(Pcre2Program program, int index) => index < 0 ? default : program.NativeBytes(program.Marks[index]);
    public static Pcre2ByteCallbacks? Wrap(Pcre2ByteCallbacks? callbacks, Pcre2HostCallbackBudget budget) => budget.Wrap(callbacks);
    public static bool HasCase(Pcre2ByteCallbacks? callbacks) => callbacks?.Case is not null;
    public static bool HasSubstitute(Pcre2ByteCallbacks? callbacks) => callbacks?.Substitute is not null;
    public static int Case(Pcre2ByteCallbacks callbacks, ReadOnlySpan<byte> input, Span<byte> output, Pcre2SubstituteCase mode) => callbacks.Case!(input, output, mode, callbacks.State);
    public static int Substitute(Pcre2ByteCallbacks callbacks, ReadOnlySpan<byte> input, ReadOnlySpan<byte> output, ReadOnlySpan<Pcre2OffsetPair> pairs, Pcre2SubstituteCalloutInfo info)
    { var context = new Pcre2ByteSubstituteContext(input, output, pairs, info); return callbacks.Substitute!(in context, callbacks.State); }
}
