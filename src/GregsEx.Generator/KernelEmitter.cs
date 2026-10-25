using System.Text;
using GregsEx.Execution.Compiled;
using GregsEx.IR;

namespace GregsEx.Generator;

// Emits the kernel CoreKernelLowering builds as an expression tree, but as C# source. Semantics are a line-for-line
// mirror of the lowering; the output depends only on the public (hidden) KernelGuard struct of GregsEx.
internal static class KernelEmitter
{
    private const int Back = 1 << 20;
    private const int Look = 2 << 20;
    private const int Limit = 3 << 20;

    // Pattern text and literals are only ever emitted as \uXXXX escapes, so no pattern byte can break out of a literal.
    internal static string Lit(string text)
    {
        using var sb = new GregsEx.Text.ValueStringBuilder(text.Length * 6 + 2);
        sb.Append('"');
        foreach (var ch in text)
        {
            sb.Append("\\u");
            sb.Append(((int)ch).ToString("X4"));
        }

        sb.Append('"');
        return sb.ToString();
    }

    internal static string EmitClass(string className, CoreKernelPlan plan, bool track)
    {
        var nodes = plan.Nodes;
        var sb = new StringBuilder();
        var fields = new StringBuilder();
        var svCount = 0;
        string Sv(IEnumerable<char> chars)
        {
            var name = "Sv" + svCount++;
            fields.AppendLine($"    private static readonly global::System.Buffers.SearchValues<char> {name} = global::System.Buffers.SearchValues.Create({Lit(new string(chars.ToArray()))});");
            return name;
        }
        string? SvOf(CharacterClass cls, out bool negated)
        {
            negated = false;
            var total = 0;
            foreach (var r in cls.Ranges.ToArray())
            { if (r.First > 0xFFFF) { break; } total += Math.Min(r.Last, 0xFFFF) - r.First + 1; }
            if (total is < 1 or > 512)
            {
                return null;
            }

            var chars = new List<char>();
            foreach (var r in cls.Ranges.ToArray())
            {
                if (r.First > 0xFFFF)
                {
                    break;
                }

                for (var c = r.First; c <= Math.Min(r.Last, 0xFFFF); c++)
                {
                    chars.Add((char)c);
                }
            }
            negated = cls.Negated;
            return Sv(chars);
        }
        var bitmaps = new Dictionary<CharacterClass, string>();
        string Test(CharacterClass cls, string chr)
        {
            var ranges = cls.Ranges.ToArray().Where(r => r.First <= 0xFFFF).Select(r => (r.First, Last: Math.Min(r.Last, 0xFFFF))).ToArray();
            if (ranges.Length == 0)
            {
                return cls.Negated ? "true" : "false";
            }

            string e;
            if (ranges.Length <= CoreKernelPlan.MaxClassRangesInline)
            {
                e = "(" + string.Join(" || ", ranges.Select(r => r.First == r.Last ? $"{chr} == {r.First}" : $"(uint)({chr} - {r.First}) <= {r.Last - r.First}u")) + ")";
            }
            else
            {
                // A larger class is a 64 Ki-bit membership bitmap built once from its ranges, like the lowering's class test.
                if (!bitmaps.TryGetValue(cls, out var name))
                {
                    bitmaps[cls] = name = "Bits" + bitmaps.Count;
                    var packed = new string(ranges.SelectMany(r => new[] { (char)r.First, (char)r.Last }).ToArray());
                    fields.AppendLine($"    private static readonly ulong[] {name} = Bitmap({Lit(packed)});");
                }

                e = $"((({name}[{chr} >> 6] >> ({chr} & 63)) & 1) != 0)";
            }
            return cls.Negated ? $"!{e}" : e;
        }

        var body = new StringBuilder();
        void L(string line) => body.AppendLine("        " + line);
        string LineTerm(string ch, int style)
            => style == 1 ? $"{ch} == 10" : $"({ch} == 10 || {ch} == 13 || (uint)({ch} - 0x2028) <= 1u)";
        string IsSurrogate(string ci) => $"(uint)({ci} - 0xD800) <= 0x7FFu";
        void Push(string idE, string bE, string cE)
        {
            L("{");
            L("    if (sp + 3 > stackLen) goto overflow;");
            L($"    Set(stack, sp, {idE}); Set(stack, sp + 1, {bE}); Set(stack, sp + 2, {cE});");
            L("    sp += 3;");
            L("}");
        }
        void PollCheck()
        {
            L("if (w >= poll) { pr = guard.Poll(w); if (pr != 0) { work = w; return pr; } poll = guard.NextPoll; }");
        }
        void Undo(int slot)
        { L("if (sp != 0)"); Push((-1 - slot).ToString(), $"Get(caps, {slot})", "0"); }
        void RestoreLim()
        {
            if (plan.HasLimitFrames)
            {
                L($"for (r = fp; ; r = Get(stack, r + 2)) {{ if (r < 0) {{ lim = len; break; }} if (Get(stack, r) >= {Limit}) {{ lim = Get(stack, r + 1); break; }} }}");
            }
        }
        int FrameId(int pc) => ((nodes[pc].Operand & LookFlags.Limit) != 0 ? Limit : Look) + pc;
        var followSv = new Dictionary<int, string>();
        string BackOff(int pc, string to)
        {
            var run = nodes[pc];
            if (run.Follow is null)
            {
                return $"({to} > c ? {to} - 1 : -1)";
            }

            if (!followSv.TryGetValue(pc, out var name))
            {
                followSv[pc] = name = Sv(run.Follow);
            }

            return $"LastInRange(input, c, {to}, {name}, {(run.FollowExcept ? "true" : "false")})";
        }

        string? prefixConst = null;
        string? firstSv = null;
        if (plan.LiteralPrefix is { } prefix)
        { prefixConst = "Prefix"; fields.AppendLine($"    private const string Prefix = {Lit(prefix)};"); }
        else if (plan.FirstSet is { Length: > 0 } f)
        {
            firstSv = Sv(f);
        }

        L($"int len = input.Length, stackLen = stack.Length, start = startAt, p = 0, sp = 0, id = 0, b = 0, c = 0, ci = 0, end = 0, resume = 0, fp = -1, r = 0, d = 0, lastStart = input.Length - {plan.MinLength}, lim = input.Length;");
        L("long w = 0, poll = guard.NextPoll;");
        L("int pr = 0;");
        if (plan.RequiredLiteral is { } required)
        {
            fields.AppendLine($"    private const string Required = {Lit(required)};");
            L($"if (startAt + {plan.RequiredLiteralMinOffset} > len || input.Slice(startAt + {plan.RequiredLiteralMinOffset}).IndexOf(Required.AsSpan(), global::System.StringComparison.Ordinal) < 0) goto noMatch;");
        }

        if (plan.RequiredSet is { Length: > 0 } requiredSet)
        {
            L($"c = input.Slice(startAt).LastIndexOfAny({Sv(requiredSet)});");
            L("if (c < 0) goto noMatch;");
            L("c += startAt;");
            L("if (c < lastStart) lastStart = c;");
        }

        if (plan.SurrogateScan)
        {
            L("if (input.IndexOfAnyInRange('\\uD800', '\\uDFFF') >= 0) goto surrogate;");
        }

        L("nextStart:");
        L("if (start > lastStart) goto noMatch;");
        L("if (w > maxSteps) goto budget;");
        PollCheck();
        if (plan.Anchored)
        {
            L("if (start > 0) goto noMatch;");
        }

        if (prefixConst is not null)
        {
            L($"c = input.Slice(start).IndexOf({prefixConst}.AsSpan(), global::System.StringComparison.Ordinal);");
            L("if (c < 0) goto noMatch;");
            L("start += c;");
        }
        else if (firstSv is not null)
        {
            L($"c = input.Slice(start).{(plan.FirstSetExcept ? "IndexOfAnyExcept" : "IndexOfAny")}({firstSv});");
            L("if (c < 0) goto noMatch;");
            L("start += c;");
            if (plan.FirstSetExcept && plan.UnicodeScalars)
            {
                L($"if (start < len && {IsSurrogate("At(input, start)")}) goto surrogate;");
            }
        }
        else if (plan.UnicodeScalars)
        {
            L($"if (start < len && {IsSurrogate("At(input, start)")}) goto surrogate;");
        }

        L("w++;");
        L("resume = 0;");
        var slots = track ? 2 * plan.GroupCount : 2;
        for (var s = 0; s < slots; s++)
        {
            L($"Set(caps, {s}, -1);");
        }

        L("sp = 0;");
        L("fp = -1;");
        L("lim = len;");
        L("p = start;");

        var order = Enumerable.Range(0, nodes.Length).Where(i => plan.Reachable[i]).ToList();
        var cases = new StringBuilder();
        foreach (var t in order.Where(i => nodes[i].Op == KernelOp.Split).Select(i => nodes[i].Alternate).Distinct())
        {
            cases.AppendLine($"            case {t}: goto L{t};");
        }

        foreach (var i in order)
        {
            if (nodes[i].Op == KernelOp.ClassRun && !nodes[i].Atomic)
            {
                cases.AppendLine($"            case {Back + i}:");
                cases.AppendLine($"                b = {BackOff(i, "p")};");
                cases.AppendLine("                if (b >= 0)");
                cases.AppendLine("                {");
                cases.AppendLine("                    if (sp + 3 > stackLen) goto overflow;");
                cases.AppendLine($"                    Set(stack, sp, {Back + i}); Set(stack, sp + 1, b); Set(stack, sp + 2, c);");
                cases.AppendLine("                    sp += 3;");
                cases.AppendLine("                }");
                cases.AppendLine($"                goto L{nodes[i].Next};");
            }
            else if (nodes[i].Op == KernelOp.BehindScan)
            {
                cases.AppendLine($"            case {Back + i}:");
                cases.AppendLine("                if (p > c)");
                cases.AppendLine("                {");
                cases.AppendLine("                    if (sp + 3 > stackLen) goto overflow;");
                cases.AppendLine($"                    Set(stack, sp, {Back + i}); Set(stack, sp + 1, p - 1); Set(stack, sp + 2, c);");
                cases.AppendLine("                    sp += 3;");
                cases.AppendLine("                }");
                cases.AppendLine($"                goto L{nodes[i].Next};");
            }
            else if (nodes[i].Op == KernelOp.LookStart)
            {
                cases.AppendLine($"            case {FrameId(i)}:");
                cases.AppendLine("                fp = c;");
                if (plan.HasLimitFrames)
                {
                    cases.AppendLine($"                for (r = fp; ; r = Get(stack, r + 2)) {{ if (r < 0) {{ lim = len; break; }} if (Get(stack, r) >= {Limit}) {{ lim = Get(stack, r + 1); break; }} }}");
                }

                cases.AppendLine((nodes[i].Operand & LookFlags.Negative) != 0 ? $"                goto L{nodes[i].Alternate};" : "                goto fail;");
            }
        }

        for (var k = 0; k < order.Count; k++)
        {
            var pc = order[k];
            int? fall = k + 1 < order.Count ? order[k + 1] : null;
            var o = nodes[pc];
            L($"L{pc}:");
            switch (o.Op)
            {
                case KernelOp.Literal when o.Backward:
                    L($"if (p <= 0 || At(input, p - 1) != '\\u{o.Operand:X4}') goto fail;");
                    L("p--; w++;");
                    break;
                case KernelOp.Literal:
                    L($"if (p >= lim || At(input, p) != '\\u{o.Operand:X4}') goto fail;");
                    L("p++; w++;");
                    break;
                case KernelOp.Any when o.Backward:
                case KernelOp.Class when o.Backward:
                    L("if (p <= 0) goto fail;");
                    L("ci = At(input, p - 1);");
                    if (o.SurrogateSensitive)
                    {
                        L($"if ({IsSurrogate("ci")}) goto surrogate;");
                    }

                    if (o.Op == KernelOp.Class)
                    {
                        L($"if (!({Test(o.Class!, "ci")})) goto fail;");
                    }
                    else if (!plan.DotAll)
                    {
                        L("if (ci == '\\n') goto fail;");
                    }

                    L("p--; w++;");
                    break;
                case KernelOp.Any:
                    L("if (p >= lim) goto fail;");
                    L("ci = At(input, p);");
                    if (o.SurrogateSensitive)
                    {
                        L($"if ({IsSurrogate("ci")}) goto surrogate;");
                    }

                    if (!plan.DotAll)
                    {
                        L("if (ci == '\\n') goto fail;");
                    }

                    L("p++; w++;");
                    break;
                case KernelOp.Class:
                    L("if (p >= lim) goto fail;");
                    L("ci = At(input, p);");
                    if (o.SurrogateSensitive)
                    {
                        L($"if ({IsSurrogate("ci")}) goto surrogate;");
                    }

                    L($"if (!({Test(o.Class!, "ci")})) goto fail;");
                    L("p++; w++;");
                    break;
                case KernelOp.LiteralRun:
                    {
                        var t = o.Text!;
                        var ok = new StringBuilder($"lim - p >= {t.Length}");
                        if (t.Length <= CoreKernelPlan.MaxLiteralRunInline)
                        {
                            for (var j = 0; j < t.Length; j++)
                            {
                                ok.Append($" && At(input, p + {j}) == '\\u{(int)t[j]:X4}'");
                            }
                        }
                        else
                        {
                            ok.Append($" && input.Slice(p, {t.Length}).SequenceEqual({Lit(t)}.AsSpan())");
                        }

                        L($"if (!({ok})) goto fail;");
                        L($"p += {t.Length}; w += {t.Length};");
                        break;
                    }
                case KernelOp.ClassRun:
                    {
                        if (o.Operand == 1)
                        {
                            if (plan.DotAll)
                            {
                                L("end = lim;");
                            }
                            else
                            {
                                L("end = input.Slice(p, lim - p).IndexOfAny('\\n'); end = end < 0 ? lim : end + p;");
                            }
                        }
                        else
                        {
                            var sv = SvOf(o.Class!, out var negated);
                            if (sv is not null)
                            {
                                if (negated)
                                {
                                    L($"end = input.Slice(p, lim - p).IndexOfAny({sv}); end = end < 0 ? lim : end + p;");
                                }
                                else
                                {
                                    L($"end = input.Slice(p, lim - p).IndexOfAnyExcept({sv}); end = end < 0 ? lim : end + p;");
                                }
                            }
                            else
                            {
                                L("end = p;");
                                L("while (end < lim) { ci = At(input, end); if (!(" + Test(o.Class!, "ci") + ")) break; end++; }");
                            }
                        }
                        if (o.SurrogateSensitive)
                        {
                            L("if (input.Slice(p, end - p).IndexOfAnyInRange('\\uD800', '\\uDFFF') >= 0) goto surrogate;");
                        }

                        if (pc == plan.LeadingRunNode)
                        {
                            L("resume = end + 1;");
                        }

                        L($"c = p + {o.Count};");
                        L("if (end < c) goto fail;");
                        L("w += end - p;");
                        if (!o.Atomic)
                        {
                            L($"b = {BackOff(pc, "end")};");
                            L("if (b >= 0)");
                            Push((Back + pc).ToString(), "b", "c");
                        }
                        L("p = end;");
                        break;
                    }
                case KernelOp.Split:
                    Push(o.Alternate.ToString(), "p", "0");
                    break;
                case KernelOp.CaptureStart:
                    if (o.Operand == 0)
                    {
                        L("Set(caps, 0, p);");
                    }
                    else if (track)
                    { Undo(2 * o.Operand); L($"Set(caps, {2 * o.Operand}, p);"); }
                    break;
                case KernelOp.CaptureEnd:
                    if (o.Operand == 0)
                    {
                        L("Set(caps, 1, p);");
                    }
                    else if (track)
                    { Undo(2 * o.Operand + 1); L($"Set(caps, {2 * o.Operand + 1}, p);"); }
                    break;
                case KernelOp.Clear:
                    if (track)
                    {
                        for (var g = o.Operand; g < o.Operand + o.Count; g++)
                        {
                            foreach (var slot in new[] { 2 * g, 2 * g + 1 })
                            {
                                L($"if (Get(caps, {slot}) != -1)");
                                L("{");
                                Undo(slot);
                                L($"    Set(caps, {slot}, -1);");
                                L("}");
                            }
                        }
                    }

                    break;
                case KernelOp.AssertStart:
                    L("if (p != 0) goto fail;");
                    break;
                case KernelOp.AssertSearchStart:
                    L("if (p != startAt) goto fail;");
                    break;
                case KernelOp.StepBack:
                    L($"if (p < {o.Count}) goto fail;");
                    L($"p -= {o.Count}; w++;");
                    break;
                case KernelOp.BehindScan:
                    L($"end = p - {o.Count};");
                    L("if (end < 0) goto fail;");
                    L(o.Operand < 0 ? "c = 0;" : $"c = p - {o.Operand} < 0 ? 0 : p - {o.Operand};");
                    L("if (end > c)");
                    Push((Back + pc).ToString(), "end - 1", "c");
                    L("p = end; w++;");
                    break;
                case KernelOp.LookStart:
                    Push(FrameId(pc).ToString(), "p", "fp");
                    L("fp = sp - 3;");
                    if ((o.Operand & LookFlags.Limit) != 0)
                    {
                        L("lim = p;");
                    }

                    break;
                case KernelOp.LookEnd when (nodes[o.Operand].Operand & LookFlags.Negative) != 0:
                    if ((nodes[o.Operand].Operand & LookFlags.Limit) != 0)
                    {
                        L("if (p != Get(stack, fp + 1)) goto fail;");
                    }

                    L("w += (sp - fp) / 3;");
                    L("for (r = sp - 3; r > fp; r -= 3) { id = Get(stack, r); if (id < 0) Set(caps, -1 - id, Get(stack, r + 1)); }");
                    L("sp = fp;");
                    L("fp = Get(stack, sp + 2);");
                    RestoreLim();
                    L("goto fail;");
                    continue;
                case KernelOp.LookEnd:
                    if ((nodes[o.Operand].Operand & LookFlags.Limit) != 0)
                    {
                        L("if (p != Get(stack, fp + 1)) goto fail;");
                    }

                    L("w += (sp - fp) / 3;");
                    L("end = Get(stack, fp + 1); c = Get(stack, fp + 2); d = fp;");
                    L("for (r = fp + 3; r < sp; r += 3) { id = Get(stack, r); if (id < 0) { Set(stack, d, id); Set(stack, d + 1, Get(stack, r + 1)); Set(stack, d + 2, Get(stack, r + 2)); d += 3; } }");
                    L("sp = d; fp = c;");
                    RestoreLim();
                    if ((nodes[o.Operand].Operand & LookFlags.Atomic) == 0)
                    {
                        L("p = end;");
                    }

                    break;
                case KernelOp.AssertEnd:
                    L("if (p != len) goto fail;");
                    break;
                case KernelOp.AssertLineStart when o.Operand == 2:
                    L("if (p != 0 && (p >= len || At(input, p - 1) != 10)) goto fail;");
                    break;
                case KernelOp.AssertLineEnd when o.Operand == 2:
                    L("if (p < len ? At(input, p) != 10 : (p != 0 && At(input, p - 1) == 10)) goto fail;");
                    break;
                case KernelOp.AssertLineEnd when o.Operand == 3:
                    L("if (p != len && (p + 1 != len || At(input, p) != 10)) goto fail;");
                    break;
                case KernelOp.AssertLineStart:
                    L($"if (p != 0 && !(({LineTerm("At(input, p - 1)", o.Operand)}))) goto fail;");
                    break;
                case KernelOp.AssertLineEnd:
                    L($"if (p != len && !(({LineTerm("At(input, p)", o.Operand)}))) goto fail;");
                    break;
                case KernelOp.WordBoundary:
                case KernelOp.NotWordBoundary:
                    {
                        var left = $"(p > 0 && ({Test(o.Class!, "At(input, p - 1)")}))";
                        var right = $"(p < len && ({Test(o.Class!, "At(input, p)")}))";
                        L($"if (!({left} {(o.Op == KernelOp.WordBoundary ? "!=" : "==")} {right})) goto fail;");
                        break;
                    }
                case KernelOp.Match:
                    L("work = w;");
                    L("return 1;");
                    continue;
            }
            if (o.Next != fall)
            {
                L($"goto L{o.Next};");
            }
        }

        L("fail:");
        L("if (sp == 0) goto advance;");
        L("w++;");
        L("if (w > maxSteps) goto budget;");
        PollCheck();
        L("sp -= 3;");
        L("id = Get(stack, sp); b = Get(stack, sp + 1); c = Get(stack, sp + 2);");
        L("if (id < 0) { Set(caps, -1 - id, b); goto fail; }");
        L("p = b;");
        if (cases.Length > 0)
        {
            L("switch (id)");
            L("{");
            body.Append(cases);
            L("    default: goto advance;");
            L("}");
        }
        else
        {
            L("goto advance;");
        }

        L("advance:");
        if (plan.Sticky)
        {
            L("goto noMatch;");
        }

        L("start++;");
        L("if (resume > start) start = resume;");
        L("goto nextStart;");
        L("overflow: work = w; return -1;");
        L("surrogate: work = w; return -2;");
        L("budget: work = w; return -3;");
        L("noMatch: work = w; return 0;");

        sb.AppendLine($"private static class {className}");
        sb.AppendLine("{");
        sb.Append(fields);
        sb.AppendLine("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] private static int At(global::System.ReadOnlySpan<char> s, int i) => global::System.Runtime.CompilerServices.Unsafe.Add(ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(s), i);");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] private static int Get(global::System.Span<int> s, int i) => global::System.Runtime.CompilerServices.Unsafe.Add(ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(s), i);");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] private static void Set(global::System.Span<int> s, int i, int v) => global::System.Runtime.CompilerServices.Unsafe.Add(ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(s), i) = v;");
        if (followSv.Count > 0)
        {
            // The last position in [from, to) holding one of values (except: a non-ASCII unit or an ASCII unit outside values), or -1.
            sb.AppendLine("    private static int LastInRange(global::System.ReadOnlySpan<char> s, int from, int to, global::System.Buffers.SearchValues<char> values, bool except)");
            sb.AppendLine("    {");
            sb.AppendLine("        var r = except ? s.Slice(from, to - from).LastIndexOfAnyExcept(values) : s.Slice(from, to - from).LastIndexOfAny(values);");
            sb.AppendLine("        return r < 0 ? -1 : r + from;");
            sb.AppendLine("    }");
        }
        if (bitmaps.Count > 0)
        {
            sb.AppendLine("    private static ulong[] Bitmap(string ranges)");
            sb.AppendLine("    {");
            sb.AppendLine("        var bits = new ulong[1024];");
            sb.AppendLine("        for (var i = 0; i < ranges.Length; i += 2) { for (int c = ranges[i], last = ranges[i + 1]; c <= last; c++) { bits[c >> 6] |= 1UL << (c & 63); } }");
            sb.AppendLine("        return bits;");
            sb.AppendLine("    }");
        }

        sb.AppendLine();
        // Fully optimized on first call: tier-0 code can allocate in vectorized span helpers, which a match must never do.
        sb.AppendLine("    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]");
        sb.AppendLine("    internal static int Run(global::System.ReadOnlySpan<char> input, int startAt, global::System.Span<int> caps, global::System.Span<int> stack, long maxSteps, ref long work, ref global::GregsEx.Execution.Compiled.KernelGuard guard)");
        sb.AppendLine("    {");
        sb.Append(body);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
