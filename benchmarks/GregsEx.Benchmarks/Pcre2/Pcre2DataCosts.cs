using BenchmarkDotNet.Attributes;
using GregsEx;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Tables;

namespace GregsEx.Benchmarks.Pcre2;

[MemoryDiagnoser]
[ShortRunJob]
public class Pcre2DataCosts
{
    private const int Count = 1024;
    private ArrayOwnerData _baseline = null!;
    [GlobalSetup]
    public void Prepare()
    {
        // Warm each exact case, including misses and supplementary UTF names.
        _baseline = new ArrayOwnerData();
        if (BindViews() <= 0 || BindArrayOwnerViews() != BindViews() || ResolveAliases() != Count * 4 || ValidateCanonicalNames() != Count * 4)
            throw new InvalidOperationException("Prepared source-data witnesses disagree.");
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int BindViews()
    {
        int checksum = 0;
        for (int i = 0; i < Count; i++)
        {
            var data = Pcre2Data.Instance.Bind(Pcre2CharacterTables.Default);
            checksum += data.Stage2[data.Stage1[i/128]*128+i%128];
        }
        return checksum;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int BindArrayOwnerViews()
    {
        int checksum = 0;
        for (int i = 0; i < Count; i++)
        {
            var data = _baseline.Bind(Pcre2CharacterTables.Default);
            checksum += data.Stage2[data.Stage1[i/128]*128+i%128];
        }
        return checksum;
    }

    [Benchmark(OperationsPerInvoke = Count * 3)]
    public int ResolveAliases()
    {
        int checksum = 0;
        for (int i = 0; i < Count; i++)
        {
            if (Pcre2Data.Instance.TryResolveProperty("Script_Extensions", "Latin", out var key)) checksum += key.Id + 1;
            if (Pcre2Data.Instance.TryResolveProperty("ASCII_Hex_Digit", default, out key)) checksum += key.Id + 1;
            if (!Pcre2Data.Instance.TryResolveProperty("not-a-property", default, out _)) checksum++;
        }
        return checksum;
    }

    [Benchmark(OperationsPerInvoke = Count * 4)]
    public int ValidateCanonicalNames()
    {
        int checksum = 0;
        for (int i = 0; i < Count; i++)
        {
            if (Pcre2Data.Instance.IsValidGroupName("_ascii42", false)) checksum++;
            if (Pcre2Data.Instance.IsValidGroupName("é\U00010400١", true)) checksum++;
            if (!Pcre2Data.Instance.IsValidGroupName("١initial", true)) checksum++;
            if (!Pcre2Data.Instance.IsValidGroupName("no-name", false)) checksum++;
        }
        return checksum;
    }

    // Fixture-only straightforward array-owner alternative. Copies are outside timing;
    // view/null/table semantics and lookup checksums are the same as production binding.
    private sealed class ArrayOwnerData
    {
        private readonly ushort[] _stage1, _stage2;
        private readonly Pcre2UnicodeRecord[] _records;
        private readonly int[] _cases;
        private readonly uint[] _scripts, _booleans;
        private readonly Pcre2SimpleCase[] _output;
        internal ArrayOwnerData()
        {
            var data = Pcre2Data.Instance.Bind(Pcre2CharacterTables.Default);
            _stage1 = data.Stage1.ToArray(); _stage2 = data.Stage2.ToArray();
            _records = data.Records.ToArray(); _cases = data.CaseSets.ToArray();
            _scripts = data.ScriptSets.ToArray(); _booleans = data.BooleanSets.ToArray();
            _output = data.OutputCases.ToArray();
        }
        internal Pcre2DataView Bind(Pcre2CharacterTables tables)
        {
            ArgumentNullException.ThrowIfNull(tables);
            return new(_stage1, _stage2, _records, _cases, _scripts, _booleans, _output, tables.Bytes);
        }
    }
}
