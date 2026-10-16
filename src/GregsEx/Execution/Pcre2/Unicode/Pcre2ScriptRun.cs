using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;

namespace GregsEx.Execution.Pcre2.Unicode;

internal readonly partial struct Pcre2UnicodeOps
{
    private enum ScriptState { Unset, Map, HanPending, HanHiraKata, HanBopomofo, HanHangul }

    private static bool Script<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        int start, int end, Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        if (start < Pcre2Kernel.ScanStart(ref state) || end < start || end > machine.VisibleEnd)
        {
            throw new InvalidOperationException("Invalid script body coordinates.");
        }

        if (start == end)
        {
            return true;
        }

        var read = TReader.Read(input, start, end, false, program.Profile, ref state);
        if (Denied(ref machine, ref state) || read.Status != Pcre2ReadStatus.Value)
        {
            return false;
        }

        var position = start + read.Width;
        // Source accepts a single character even when its script is Unknown.
        if (position == end)
        {
            return true;
        }

        Span<uint> required = stackalloc uint[Pcre2DataAbi.ScriptWords];
        Span<uint> map = stackalloc uint[Pcre2DataAbi.ScriptWords];
        required.Clear();
        var requirement = ScriptState.Unset;
        var digits = 0;
        while (true)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, position))
            {
                return false;
            }

            var record = Pcre2CharacterOps.Record(read.Value, data);
            var script = (Pcre2Script)record.Script;
            if (script == Pcre2Script.Unknown)
            {
                return false;
            }
            // The provider replaces a source zero-extension map with the primary
            // script's bit. Common/Inherited are above Unknown and cannot appear
            // in source extension lists; their bit therefore identifies precisely
            // the source zero-extension case (not the normalized word offset).
            data.ScriptSets.Slice(record.ScriptSetOffset, Pcre2DataAbi.ScriptWords).CopyTo(map);
            if (script is not (Pcre2Script.Common or Pcre2Script.Inherited) || !Has(map, script))
            {
                if (script is not (Pcre2Script.Common or Pcre2Script.Inherited))
                {
                    map[record.Script / 32] |= 1u << (record.Script % 32);
                }

                switch (requirement)
                {
                    case ScriptState.Unset:
                        requirement = Special(script);
                        if (requirement == ScriptState.Map)
                        {
                            map.CopyTo(required);
                        }

                        break;
                    case ScriptState.HanPending:
                        if (script != Pcre2Script.Han)
                        {
                            var special = (Has(map, Pcre2Script.Bopomofo) ? 1 : 0) |
                                (Has(map, Pcre2Script.Hiragana) ? 2 : 0) | (Has(map, Pcre2Script.Katakana) ? 4 : 0) |
                                (Has(map, Pcre2Script.Hangul) ? 8 : 0);
                            if (special == 0)
                            {
                                return false;
                            }

                            if (special == 1)
                            {
                                requirement = ScriptState.HanBopomofo;
                            }
                            else if (special == 6)
                            {
                                requirement = ScriptState.HanHiraKata;
                            }
                            // Including Hangul alone stays pending: this exact
                            // asymmetric source state machine is intentional.
                        }
                        break;
                    case ScriptState.HanHiraKata:
                        if (!Has(map, Pcre2Script.Han) && !Has(map, Pcre2Script.Hiragana) && !Has(map, Pcre2Script.Katakana))
                        {
                            return false;
                        }

                        break;
                    case ScriptState.HanBopomofo:
                        if (!Has(map, Pcre2Script.Han) && !Has(map, Pcre2Script.Bopomofo))
                        {
                            return false;
                        }

                        break;
                    case ScriptState.HanHangul:
                        if (!Has(map, Pcre2Script.Han) && !Has(map, Pcre2Script.Hangul))
                        {
                            return false;
                        }

                        break;
                    case ScriptState.Map:
                        var intersects = false;
                        for (var i = 0; i < Pcre2DataAbi.ScriptWords; i++)
                        {
                            intersects |= (required[i] & map[i]) != 0;
                        }

                        if (!intersects)
                        {
                            return false;
                        }

                        requirement = Special(script);
                        if (requirement == ScriptState.Map)
                        {
                            for (var i = 0; i < Pcre2DataAbi.ScriptWords; i++)
                            {
                                required[i] &= map[i];
                            }
                        }

                        break;
                }
            }
            // DigitSet was normalized from source ucd_digit_sets by the verified
            // provider. It is the source binary-search family ID, not a script ID.
            if (record.ParticularCategory == (int)Pcre2ParticularCategory.Nd)
            {
                if (digits == 0)
                {
                    digits = record.DigitSet;
                }
                else if (digits != record.DigitSet)
                {
                    return false;
                }
            }
            if (position == end)
            {
                return true;
            }

            read = TReader.Read(input, position, end, false, program.Profile, ref state);
            if (Denied(ref machine, ref state) || read.Status != Pcre2ReadStatus.Value)
            {
                return false;
            }

            position += read.Width;
        }
    }

    private static bool Has(ReadOnlySpan<uint> map, Pcre2Script script) => (map[(int)script / 32] & (1u << ((int)script % 32))) != 0;
    private static ScriptState Special(Pcre2Script script) => script switch
    {
        Pcre2Script.Han => ScriptState.HanPending,
        Pcre2Script.Hiragana or Pcre2Script.Katakana => ScriptState.HanHiraKata,
        Pcre2Script.Bopomofo => ScriptState.HanBopomofo,
        Pcre2Script.Hangul => ScriptState.HanHangul,
        _ => ScriptState.Map
    };
}
