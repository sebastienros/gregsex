// Records native Oniguruma results for the Oniguruma corpus (test/corpora/oniguruma). It calls Onigwrap's native
// onigwrap_create/onigwrap_search directly (ONIG_OPTION_CAPTURE_GROUP, ONIG_SYNTAX_DEFAULT, UTF-16LE), so patterns
// reach Oniguruma exactly as written; Onigwrap's managed \xHH rewrite is not applied.
//
//   cases <input.json> <output.json>   input: [{ "pattern", "subjects": [..], "starts": [..]? }]
//   trace <trace.json> <output.json>   input: { language: { patterns, scanners, lines, calls } }
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: cases|trace <input.json> <output.json>");
    return 2;
}

var input = JsonNode.Parse(File.ReadAllText(args[1]))!;
JsonNode output = args[0] switch
{
    "cases" => Cases(input.AsArray()),
    "trace" => Trace(input.AsObject()),
    _ => throw new ArgumentException(args[0]),
};
File.WriteAllText(args[2], output.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
return 0;

// Per case: null when the pattern does not compile, else per subject and start: null (no match), an error code, or
// the capture pairs (start, end; -1 when a group did not participate).
static JsonArray Cases(JsonArray cases)
{
    var result = new JsonArray();
    foreach (var item in cases)
    {
        var pattern = item!["pattern"]!.GetValue<string>();
        var regex = Native.Create(pattern);
        if (regex == IntPtr.Zero)
        {
            result.Add(null);
            continue;
        }

        var subjects = new JsonArray();
        foreach (var subject in item["subjects"]!.AsArray())
        {
            var text = Text(subject!);
            var starts = item["starts"] is JsonArray s ? s.Select(x => x!.GetValue<int>()).Where(x => x <= text.Length).ToArray()
                : Enumerable.Range(0, text.Length + 1).ToArray();
            var outcomes = new JsonArray();
            foreach (var start in starts)
            {
                outcomes.Add(new JsonArray(start, Native.Search(regex, text, start)));
            }
            subjects.Add(outcomes);
        }
        result.Add(subjects);
    }
    return result;
}

// Per language and call: the index of the selected pattern (OnigScanner.FindNextMatchSync: earliest start, first
// pattern on a tie, stopping at a match at the offset) and its captures, or null.
static JsonObject Trace(JsonObject trace)
{
    var result = new JsonObject();
    foreach (var (language, node) in trace)
    {
        var patterns = node!["patterns"]!.AsArray().Select(p => Native.Create(p!.GetValue<string>())).ToArray();
        var scanners = node["scanners"]!.AsArray().Select(s => s!.AsArray().Select(i => i!.GetValue<int>()).ToArray()).ToArray();
        var lines = node["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray();
        var calls = node["calls"]!.AsArray().Select(c => c!.GetValue<int>()).ToArray();
        var outcomes = new JsonArray();
        for (var c = 0; c < calls.Length; c += 3)
        {
            var (scanner, line, position) = (calls[c], calls[c + 1], calls[c + 2]);
            JsonNode? best = null;
            var bestStart = int.MaxValue;
            for (var i = 0; i < scanners[scanner].Length; i++)
            {
                if (Native.Search(patterns[scanners[scanner][i]], lines[line], position) is not JsonArray captures)
                {
                    continue;
                }

                var start = captures[0]!.GetValue<int>();
                if (start < bestStart)
                {
                    bestStart = start;
                    best = new JsonArray(i, captures);
                }

                if (start == position)
                {
                    break;
                }
            }
            outcomes.Add(best);
        }
        result[language] = outcomes;
    }
    return result;
}

// A subject is a JSON string, or { "units": [...] } for UTF-16 text JSON cannot carry (lone surrogates).
static string Text(JsonNode subject)
    => subject is JsonObject o ? new string(o["units"]!.AsArray().Select(u => (char)u!.GetValue<int>()).ToArray()) : subject.GetValue<string>();

static unsafe class Native
{
    [DllImport("onigwrap")] private static extern IntPtr onigwrap_create(char* pattern, int length, int ignoreCase, int multiline);
    [DllImport("onigwrap")] private static extern IntPtr onigwrap_region_new();
    [DllImport("onigwrap")] private static extern int onigwrap_search(IntPtr regex, char* text, int offset, int length, IntPtr region);
    [DllImport("onigwrap")] private static extern int onigwrap_num_regs(IntPtr region);
    [DllImport("onigwrap")] private static extern int onigwrap_pos(IntPtr region, int nth);
    [DllImport("onigwrap")] private static extern int onigwrap_len(IntPtr region, int nth);

    private static readonly IntPtr Region = onigwrap_region_new();

    public static IntPtr Create(string pattern)
    {
        fixed (char* p = pattern)
        {
            return onigwrap_create(p, pattern.Length * 2, 0, 0);
        }
    }

    // Null for no match, a negative Oniguruma error code (such as the retry limit), or the capture pairs.
    public static JsonNode? Search(IntPtr regex, string text, int start)
    {
        int r;
        fixed (char* t = text)
        {
            r = onigwrap_search(regex, t, start * 2, text.Length * 2, Region);
        }

        if (r == -1)
        {
            return null;
        }

        if (r < 0)
        {
            return JsonValue.Create(r);
        }

        var captures = new JsonArray();
        for (var g = 0; g < onigwrap_num_regs(Region); g++)
        {
            var pos = onigwrap_pos(Region, g);
            captures.Add(pos);
            captures.Add(pos < 0 ? -1 : pos + onigwrap_len(Region, g));
        }
        return captures;
    }
}
