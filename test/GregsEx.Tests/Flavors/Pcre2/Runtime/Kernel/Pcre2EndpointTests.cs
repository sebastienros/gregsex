using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

public sealed class Pcre2EndpointTests
{
    [Theory]
    [InlineData(8, 0)]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(8, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 0)]
    [InlineData(16, 1)]
    [InlineData(16, 2)]
    [InlineData(16, 3)]
    [InlineData(16, 4)]
    public void IndependentNativeFragmentAndScanEndRoles(int width, int caseId)
    {
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, Pcre2CompileFlags.MatchInvalidUtf);
        var p = new KernelPlan(profile);
        var scanChild = -1;
        if (caseId == 4)
        {
            var capture = p.Capture(p.Literal('a'));
            scanChild = p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
            p.Seq(capture, p.Scan(scanChild));
        }
        else if (caseId == 0)
        {
            p.Literal('a');
        }
        else
        {
            p.Seq(p.Literal('a'), p.Leaf(caseId switch { 1 => Pcre2NodeKind.AbsoluteEnd, 2 => Pcre2NodeKind.BeforeFinalNewline, _ => Pcre2NodeKind.End }));
        }

        var fixture = new KernelFixture(p.Build());
        var result = width == 8 ? Run<byte, Pcre2ByteTextReader>(fixture, caseId == 4 ? "ab"u8.ToArray() : [97, 255, 98], caseId, scanChild) :
            Run<char, Pcre2CharTextReader>(fixture, caseId == 4 ? new char[] { 'a', 'b' } : ['a', '\uD800', 'b'], caseId, scanChild);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "test/corpora/pcre2/endpoints/endpoint-native.jsonl")))
        {
            root = root.Parent;
        }

        var native = File.ReadAllLines(Path.Combine(root!.FullName, "test/corpora/pcre2/endpoints/endpoint-native.jsonl"))
            .Select(line => JsonDocument.Parse(line)).Single(d => d.RootElement.GetProperty("width").GetInt32() == width && d.RootElement.GetProperty("case").GetInt32() == caseId);
        using (native)
        {
            Assert.Equal(native.RootElement.GetProperty("rc").GetInt32(), result.NativeReturnCode);
            if (result.Status == Pcre2MatchStatus.Match)
            {
                Assert.Equal(native.RootElement.GetProperty("startChar").GetInt32(), result.StartChar);
            }
        }
    }
    [Theory]
    [InlineData(8, 0, 1)]
    [InlineData(8, 1, -2)]
    [InlineData(8, 2, -2)]
    [InlineData(8, 3, -2)]
    [InlineData(8, 4, -2)]
    [InlineData(16, 0, 1)]
    [InlineData(16, 1, -2)]
    [InlineData(16, 2, -2)]
    [InlineData(16, 3, -2)]
    [InlineData(16, 4, -2)]
    public void SourceHardPartialAtEmptyBoundary(int width, int caseId, int nativeReturn)
    {
        var p = new KernelPlan(new((Pcre2CodeUnitWidth)width));
        if (caseId == 4)
        {
            p.Repeat(p.Literal('a'));
        }
        else
        {
            p.Leaf(caseId switch { 0 => Pcre2NodeKind.Empty, 1 => Pcre2NodeKind.AbsoluteEnd, 2 => Pcre2NodeKind.BeforeFinalNewline, _ => Pcre2NodeKind.End });
        }

        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.PartialHard };
        var result = width == 8 ? fixture.Match([], pairs, options: options) : fixture.Match("", pairs, options: options);
        Assert.Equal(nativeReturn, result.NativeReturnCode);
        Assert.Equal(new(true, 0, 0), pairs[0]);
    }
    private static Pcre2MatchResult Run<TUnit, TReader>(KernelFixture fixture, ReadOnlySpan<TUnit> input, int caseId, int scanChild)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var machine = new Pcre2VmState(fixture.Capacity, new(), input.Length);
        var state = fixture.State(machine.Limits);
        var profile = fixture.Program.Profile;
        var data = Pcre2Data.Instance.Bind(profile.CharacterTables);
        var flags = caseId == 0 ? Pcre2MatchFlags.Endanchored : Pcre2MatchFlags.None;
        Pcre2TextWindow window;
        // Concrete native fragment windows retain original coordinates.
        if (typeof(TUnit) == typeof(byte))
        {
            window = Pcre2ByteTextReader.Bind(System.Runtime.InteropServices.MemoryMarshal.Cast<TUnit, byte>(input), 0, 0, profile, flags, Pcre2ValidationPolicy.NativeWindow, ref state);
        }
        else
        {
            window = Pcre2CharTextReader.Bind(System.Runtime.InteropServices.MemoryMarshal.Cast<TUnit, char>(input), 0, 0, profile, flags, Pcre2ValidationPolicy.NativeWindow, ref state);
        }

        Assert.True(window.Available);
        machine.VisibleEnd = window.VisibleEnd;
        machine.CandidateStart = window.CandidateStart;
        var options = new Pcre2MatchOptions { Flags = flags | window.FragmentFlags };
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, window.ScanStart, ref machine, ref state));
        var storage = new Pcre2Storage(state.Buffers, fixture.Capacity, fixture.Program.GroupCount);
        while (true)
        {
            var step = Pcre2Kernel.Step<TUnit, TReader>(fixture.Program, input, data, options, ref machine, ref state);
            if (step.Kind == Pcre2StepKind.Continue)
            {
                continue;
            }

            if (step.Kind == Pcre2StepKind.EnterAssertion && caseId == 4)
            {
                var capture = storage.Capture(1);
                Assert.True(storage.PushWindow(new(capture.End, capture.Start, machine.Node, machine.Position, machine.FrameCount, machine.CaptureCursor), ref machine, ref state));
                Assert.True(Pcre2Kernel.RefreshWindow(fixture.Program, ref machine, ref state));
                Assert.True(Pcre2Kernel.EnterModuleChild(fixture.Program, scanChild, Pcre2FrameKind.Scan, ref machine, ref state));
                machine.Position = capture.Start;
                continue;
            }
            if (step.Kind == Pcre2StepKind.Return && caseId == 4)
            {
                Assert.True(storage.PopWindow(ref machine, ref state, out var saved));
                machine.Position = saved.SavedPosition;
                Assert.True(Pcre2Kernel.RefreshWindow(fixture.Program, ref machine, ref state));
                Assert.True(Pcre2Kernel.LeaveModuleChild(fixture.Program, true, true, ref machine, ref state));
                continue;
            }
            Assert.True(step.Kind is Pcre2StepKind.Accept or Pcre2StepKind.Backtrack);
            var pairs = new Pcre2OffsetPair[4];
            var result = Pcre2Kernel.Publish(fixture.Program, ref machine, ref state, pairs);
            if (result.Status == Pcre2MatchStatus.Match)
            {
                Assert.Equal(new(true, 0, 1), pairs[0]);
            }

            return result;
        }
    }
}
