#nullable enable
namespace GregsEx;
/// <summary>Structured native/managed failure from an allocating substitution convenience.</summary>
public sealed class Pcre2SubstituteException : InvalidOperationException
{
    /// <summary>Complete underlying substitution result, including managed safety failure.</summary>
    public Pcre2SubstituteResult Result { get; }
    /// <summary>Create only from a failed native or managed operation.</summary>
    public Pcre2SubstituteException(Pcre2SubstituteResult result) : base($"PCRE2 substitution failed: {result.NativeReturnCode}, {result.Failure.Code}.")
    { if (result.NativeReturnCode >= 0 && result.Failure.Code == RegexExecutionFailureCode.None) { throw new ArgumentException("A failing result is required.", nameof(result)); } Result = result; }
}
