using GregsEx.Contracts;

namespace GregsEx.Text;

internal static class TextValidation
{
    internal static void Admit(int length, int startAt, ProfileDescriptor profile, bool chars)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if ((uint)startAt > (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(startAt));
        }

        if (chars ? profile.InputKind != RegexInputKind.Utf16 : profile.InputKind == RegexInputKind.Utf16)
        {
            throw new ArgumentException("Reader and input representation must agree.", nameof(profile));
        }
    }

    internal static RegexSearchStatus FailureStatus(ref RunState state) => state.Failure.Code switch
    {
        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
        RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidStartBoundary => RegexSearchStatus.InvalidInput,
        _ => RegexSearchStatus.LimitExceeded
    };

    internal static RegexSearchStatus Invalid(ref RunState state, int offset, bool boundary = false)
    {
        state.Failure = new(boundary ? RegexExecutionFailureCode.InvalidStartBoundary :
            RegexExecutionFailureCode.InvalidEncoding, offset, state.Budget.WorkConsumed);
        return RegexSearchStatus.InvalidInput;
    }
}
