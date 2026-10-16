using System.Buffers;
using System.Text;
using GregsEx;
using GregsEx.Contracts;
using GregsEx.Execution.Thompson;
using GregsEx.IR;
namespace AllocationPolicy;
internal static class ThompsonFixture
{
    private static CoreInstruction I(CoreOpcode op, int x = 0, int y = 0, int operand = 0, int count = 0)
        => new(op, x, y, operand, count, new(0, 0, RegexOffsetUnit.Utf16CodeUnits));

    private static SemanticProgram Program(CoreInstruction[] body, int groups = 1,
        RegularCoreMode mode = RegularCoreMode.Utf16CodeUnits, RegexInputKind kind = RegexInputKind.Utf16,
        RegularCoreFlags flags = RegularCoreFlags.None, CharacterClass[]? classes = null)
    {
        Check.That(ProfileDescriptor.TryCreate(new RegularCoreProfile(mode, flags), kind, out var profile, out _));
        CoreInstruction[] code = [I(CoreOpcode.CaptureStart, 1), .. body,
            I(CoreOpcode.CaptureEnd, body.Length + 2), I(CoreOpcode.Match)];
        var budget = new CompileBudget(new());
        Check.That(SemanticProgram.TryCreate(code, classes ?? [], profile!, groups, 0,
            RegexOffsetUnit.Utf16CodeUnits, ref budget, out var program, out var diagnostic), diagnostic.ToString());
        return program!;
    }

    private static SemanticProgram Literal(int value = 'a', RegularCoreMode mode = RegularCoreMode.Utf16CodeUnits,
        RegexInputKind kind = RegexInputKind.Utf16)
        => Program([I(CoreOpcode.Literal, 2, operand: value)], mode: mode, kind: kind);

    private static SemanticProgram Star(bool lazy = false, bool capture = false)
        => capture
            ? Program([I(CoreOpcode.Split, lazy ? 6 : 2, lazy ? 2 : 6),
                I(CoreOpcode.ClearCaptures, 3, operand: 1, count: 1), I(CoreOpcode.CaptureStart, 4, operand: 1),
                I(CoreOpcode.Literal, 5, operand: 'a'), I(CoreOpcode.CaptureEnd, 1, operand: 1)], 2)
            : Program([I(CoreOpcode.Split, lazy ? 3 : 2, lazy ? 2 : 3), I(CoreOpcode.Literal, 1, operand: 'a')]);

    internal static Workload Create(int family, int operationValue)
    {
        var op=(SearchOperation)operationValue;
        bool raw=family==2;
        var h=new Harness(family==0 ? Star(false,true) : Literal(raw?255:0x1F600,
            raw?RegularCoreMode.RawBytes:RegularCoreMode.UnicodeScalars,
            family==1?RegexInputKind.Utf8:raw?RegexInputKind.Bytes:RegexInputKind.Utf16),op);
        string[] chars=family==0?["","bbb","baaa","aaa"]:["","éz","x😀z"];
        byte[][] bytes=raw?[[],[0,255,128],[0,1,128]]:[[],Encoding.UTF8.GetBytes("x😀z"),Encoding.UTF8.GetBytes("ézz")];
        if(family is 1 or 2)
        {
            Check.Equal(RegexSearchStatus.NoMatch,h.Bytes(bytes[0],0,out _));
            Check.Equal(RegexSearchStatus.Match,h.Bytes(bytes[1],0,out var hit));
            if(op!=SearchOperation.Boolean) Check.Equal(new RegexMatchRange(1,raw?1:4),hit);
            Check.Equal(RegexSearchStatus.NoMatch,h.Bytes(bytes[2],0,out _));
        }
        else
        {
            Check.Equal(RegexSearchStatus.Match,h.Char(chars[^1],0,out var hit));
            if(op!=SearchOperation.Boolean) Check.Equal(family==0?new RegexMatchRange(0,3):new RegexMatchRange(1,2),hit);
        }
        return new Workload(Cycle,warmCount:family==3?100:16384,metadata:new{family,operation=op,chars=chars.Select(InputIdentity.Chars).ToArray(),bytes=bytes.Select(InputIdentity.Bytes).ToArray()});
        long Cycle()
        {
            long sum=0;
            if(family is 1 or 2) foreach(var input in bytes) sum+=(int)h.Bytes(input,0,out var r)+r.Index+r.Length;
            else foreach(var input in chars) sum+=(int)h.Char(input,0,out var r)+r.Index+r.Length;
            return sum;
        }
    }
    // Test-only readers are independent of production text readers. Production integration binds its own
    // validated readers; these make lane behavior executable without sibling implementation.
    private readonly struct CharReader : ITextReader<char>
    {
        public static RegexSearchStatus Validate(ReadOnlySpan<char> input, int startAt, ProfileDescriptor profile, ref RunState state)
        {
            for (var offset = 0; offset < input.Length;)
            {
                var valid = TryRead(input, offset, profile, out _, out var width);
                if (!state.TryCharge(valid ? width : 1, offset)) return Status(state);
                if (!valid) { state.Failure = new(RegexExecutionFailureCode.InvalidEncoding, offset, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
                offset += width;
            }
            if (profile.TextModel == TextModel.UnicodeScalars && startAt > 0 && startAt < input.Length && char.IsLowSurrogate(input[startAt]))
            { state.Failure = new(RegexExecutionFailureCode.InvalidStartBoundary, startAt, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
            return RegexSearchStatus.NoMatch;
        }
        public static bool TryRead(ReadOnlySpan<char> input, int offset, ProfileDescriptor profile, out int value, out int width)
        {
            value = width = 0;
            if (offset == input.Length) return false;
            var first = input[offset]; value = first; width = 1;
            if (profile.TextModel != TextModel.UnicodeScalars || !char.IsSurrogate(first)) return true;
            if (!char.IsHighSurrogate(first) || offset + 1 == input.Length || !char.IsLowSurrogate(input[offset + 1])) { width = 0; return false; }
            value = char.ConvertToUtf32(first, input[offset + 1]); width = 2; return true;
        }
    }

    private readonly struct ByteReader : ITextReader<byte>
    {
        public static RegexSearchStatus Validate(ReadOnlySpan<byte> input, int startAt, ProfileDescriptor profile, ref RunState state)
        {
            for (var offset = 0; offset < input.Length;)
            {
                var valid = TryRead(input, offset, profile, out _, out var width);
                if (!state.TryCharge(valid ? width : 1, offset)) return Status(state);
                if (!valid) { state.Failure = new(RegexExecutionFailureCode.InvalidEncoding, offset, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
                offset += width;
            }
            if (profile.TextModel == TextModel.UnicodeScalars && startAt < input.Length && (input[startAt] & 0xC0) == 0x80)
            { state.Failure = new(RegexExecutionFailureCode.InvalidStartBoundary, startAt, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
            return RegexSearchStatus.NoMatch;
        }
        public static bool TryRead(ReadOnlySpan<byte> input, int offset, ProfileDescriptor profile, out int value, out int width)
        {
            value = width = 0;
            if (offset == input.Length) return false;
            if (profile.TextModel == TextModel.RawBytes) { value = input[offset]; width = 1; return true; }
            if (Rune.DecodeFromUtf8(input[offset..], out var rune, out width) != OperationStatus.Done) { width = 0; return false; }
            value = rune.Value; return true;
        }
    }

    private static RegexSearchStatus Status(in RunState state) => state.Failure.Code switch
    {
        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
        _ => RegexSearchStatus.LimitExceeded
    };

    private sealed class Harness
    {
        internal readonly ThompsonExecutor Executor;
        private readonly SearchOperation _operation;
        private readonly int[] _states, _captures, _scratch;
        private readonly RegexMatchLimits _limits = new();
        internal readonly RegexCapture[] Groups;
        internal RegexExecutionFailure Failure;
        internal long Work;
        internal Harness(SemanticProgram program, SearchOperation operation = SearchOperation.Captures)
        {
            Executor = new(program); _operation = operation;
            var c = Executor.GetRequiredCapacity(operation);
            _states = new int[c.StateSlots]; _captures = new int[c.CaptureSlots]; _scratch = new int[c.ScratchSlots];
            Groups = new RegexCapture[program.GroupCount + 1];
        }
        internal RegexSearchStatus Char(ReadOnlySpan<char> input, int start, out RegexMatchRange range,
            long? maxWork = null, RegexMatchOptions options = default, int? destinationLength = null)
        {
            var state = new RunState(new(_states, _captures, _scratch), maxWork is { } work ? new(work) : _limits, options);
            var status = Executor.Search<char, CharReader>(input, start, _operation, ref state,
                destinationLength is { } length ? Groups.AsSpan(0, length) : Groups, out range);
            Failure = state.Failure; Work = state.Budget.WorkConsumed; return status;
        }
        internal RegexSearchStatus Bytes(ReadOnlySpan<byte> input, int start, out RegexMatchRange range)
        {
            var state = new RunState(new(_states, _captures, _scratch), _limits);
            var status = Executor.Search<byte, ByteReader>(input, start, _operation, ref state, Groups, out range);
            Failure = state.Failure; Work = state.Budget.WorkConsumed; return status;
        }
    }
}
