using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Json.Schema;

var cli = new RootCommand("Development-only POSIX corpus and pinned libc audit. Run from repository root.");
foreach (var name in new[] { "generate", "run", "verify", "prepare", "supplement", "schema" })
{
    var cmd = new Command(name);
    var python = new Option<string>("--python") { DefaultValueFactory = _ => "python3", Description = "Python executable for fixed internal procedures." };
    cmd.Add(python);
    cmd.SetAction(result => Execute(name, result.GetValue(python)!));
    cli.Add(cmd);
}
return cli.Parse(args).Invoke();

static int Execute(string name, string python)
{
    var root = Directory.GetCurrentDirectory();
    if (!File.Exists(Path.Combine(root, "GregsEx.slnx"))) throw new InvalidOperationException("Run from repository root.");
    if (name == "schema")
    {
        var schema = JsonSchema.FromText(File.ReadAllText("test/GregsEx.Tests/Contracts/Posix/PosixFixtures.schema.json"));
        foreach (var path in new[] { "test/GregsEx.Tests/Contracts/Posix/ContractVectors.json", "test/corpora/posix/expanded.json" })
        {
            using var corpus = JsonDocument.Parse(File.ReadAllText(path));
            var evaluated = schema.Evaluate(corpus.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true });
            if (!evaluated.IsValid) throw new InvalidDataException(JsonSerializer.Serialize(evaluated));
            Console.WriteLine($"{path}: {corpus.RootElement.GetProperty("cases").GetArrayLength()} fixtures pass frozen Draft2020-12 schema.");
        }
        return 0;
    }
    var start = new ProcessStartInfo(python) { UseShellExecute = false, WorkingDirectory = root };
    start.ArgumentList.Add(Path.Combine(root, "tools/oracles/posix", name + ".py"));
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch fixed procedure.");
    if (!process.WaitForExit(300_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); return 124; }
    return process.ExitCode;
}
