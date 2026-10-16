using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GregsEx.Benchmarks.Acceleration.Corpus;

/// <summary>One pinned measurement case read from docs/benchmarks/release-manifest.json.</summary>
public sealed record CorpusCase(string Id, string Category, string Pattern, string[] Profiles, string[] Operations,
    string Generator, long Seed, int Length, string Text, string Suffix, bool Newlines, (string Text, double At)[] Inject,
    Dictionary<string, string> Flags)
{
    public string Generate() => CorpusGenerator.Generate(this);
}

/// <summary>Deterministic text generators from the release manifest. Lengths count UTF-16 code units.</summary>
public static class CorpusGenerator
{
    private static readonly string[] Prose =
    [
        "the", "of", "and", "to", "in", "is", "that", "for", "it", "was", "on", "with", "as", "his", "they", "be",
        "at", "one", "have", "this", "from", "or", "had", "by", "but", "some", "what", "there", "we", "can", "out", "other",
        "were", "all", "your", "when", "up", "use", "word", "how", "said", "an", "each", "she", "which", "do", "their", "time",
        "running", "thing", "morning", "going", "making", "singing", "reading", "walking", "river", "stone", "garden", "window", "paper", "winter", "silver", "market",
    ];
    private static readonly string[] Cyrillic = ["привет", "мир", "река", "город", "дорога", "солнце", "книга", "зима"];
    private static readonly string[] Greek = ["κόσμος", "φως", "θάλασσα", "άνθρωπος", "βιβλίο", "ήλιος", "νύχτα", "δρόμος"];
    private static readonly string[] Cjk = ["世界", "你好", "日本語", "漢字", "東京", "文字", "言葉", "電車"];
    private static readonly string[] Names = ["alice", "bob", "carol", "dave", "erin", "frank", "grace", "heidi"];
    private static readonly string[] Levels = ["INFO", "WARN", "ERROR", "DEBUG"];

    public static int ProseVocabulary => Prose.Length;

    public static IReadOnlyList<CorpusCase> LoadCases(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var list = new List<CorpusCase>();
        foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var inject = new List<(string, double)>();
            if (input.TryGetProperty("inject", out var injectNode))
            {
                foreach (var item in injectNode.EnumerateArray())
                {
                    inject.Add((item.GetProperty("text").GetString()!, item.GetProperty("at").GetDouble()));
                }
            }

            var flags = new Dictionary<string, string>();
            if (c.TryGetProperty("flags", out var flagNode))
            {
                foreach (var f in flagNode.EnumerateObject())
                {
                    flags[f.Name] = f.Value.GetString()!;
                }
            }

            list.Add(new CorpusCase(
                c.GetProperty("id").GetString()!, c.GetProperty("category").GetString()!, c.GetProperty("pattern").GetString()!,
                c.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()!).ToArray(),
                c.GetProperty("operations").EnumerateArray().Select(p => p.GetString()!).ToArray(),
                input.GetProperty("generator").GetString()!,
                input.TryGetProperty("seed", out var seed) ? seed.GetInt64() : 0,
                input.GetProperty("length").GetInt32(),
                input.TryGetProperty("text", out var text) ? text.GetString()! : "",
                input.TryGetProperty("suffix", out var suffix) ? suffix.GetString()! : "",
                !input.TryGetProperty("newlines", out var newlines) || newlines.GetBoolean(),
                inject.ToArray(), flags));
        }
        return list;
    }

    public static string Generate(CorpusCase c)
    {
        var body = c.Generator switch
        {
            "prose-v1" => Words(c.Seed, c.Length - c.Suffix.Length, c.Newlines, unicode: false),
            "unicode-mixed-v1" => Words(c.Seed, c.Length - c.Suffix.Length, c.Newlines, unicode: true),
            "log-v1" => Log(c.Seed, c.Length - c.Suffix.Length),
            "repeat-v1" => Repeat(c.Text, c.Length),
            _ => throw new InvalidOperationException($"Unknown generator {c.Generator}."),
        };
        if (c.Generator == "repeat-v1")
        {
            return body + c.Suffix;
        }

        var builder = new StringBuilder(body);
        foreach (var (text, at) in c.Inject)
        {
            var position = Math.Clamp((int)(at * builder.Length), 0, Math.Max(0, builder.Length - text.Length));
            builder.Remove(position, text.Length).Insert(position, text);
        }
        if (c.Suffix.Length > 0)
        {
            builder.Append(c.Suffix);
        }

        return builder.ToString();
    }

    public static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Repeat(string text, int length)
    {
        var builder = new StringBuilder(length);
        while (builder.Length < length)
        {
            builder.Append(text);
        }

        return builder.ToString(0, length);
    }

    private static string Words(long seed, int length, bool newlines, bool unicode)
    {
        var rng = new SplitMix64((ulong)seed);
        var builder = new StringBuilder(length + 16);
        var word = 0;
        while (builder.Length < length)
        {
            var source = Prose;
            if (unicode && word % 5 == 4)
            {
                source = (word / 5 % 3) switch { 0 => Cyrillic, 1 => Greek, _ => Cjk };
            }

            builder.Append(source[(int)(rng.Next() % (ulong)source.Length)]);
            word++;
            builder.Append(newlines && word % 12 == 0 ? '\n' : ' ');
        }
        return builder.ToString(0, length);
    }

    private static string Log(long seed, int length)
    {
        var rng = new SplitMix64((ulong)seed);
        var builder = new StringBuilder(length + 96);
        while (builder.Length < length)
        {
            ulong a = rng.Next(), b = rng.Next();
            builder.Append(Pad((int)(2020 + a % 6), 4)).Append('-').Append(Pad((int)(1 + a / 7 % 12), 2)).Append('-')
                .Append(Pad((int)(1 + a / 91 % 28), 2)).Append(' ').Append(Pad((int)(a / 3000 % 24), 2)).Append(':')
                .Append(Pad((int)(a / 70000 % 60), 2)).Append(':').Append(Pad((int)(a / 4200000 % 60), 2)).Append(' ')
                .Append(Levels[(int)(b % 4)]).Append(" user=").Append(Names[(int)(b / 4 % 8)]).Append(" id=").Append((b / 32 % 100000).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" msg=").Append(Prose[(int)(b / 3200000 % (ulong)Prose.Length)]).Append('\n');
        }
        return builder.ToString(0, length);
    }

    private static string Pad(int value, int width) => value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');

    private struct SplitMix64(ulong state)
    {
        public ulong Next()
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
