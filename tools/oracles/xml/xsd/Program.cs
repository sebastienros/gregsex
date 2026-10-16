using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Json.Schema;

var cli = new RootCommand("Pinned independent XSD1.1 oracle preparation and verification. No product execution or measurements.");
foreach (var name in new[] { "generate", "run", "verify", "schema" })
{
    var cmd = new Command(name);
    var python = new Option<string>("--python") { Description = "Python executable used only for fixed internal scripts.", DefaultValueFactory = _ => "/opt/homebrew/bin/python3" };
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
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(root, "test/GregsEx.Tests/Contracts/Xml/XmlFixtures.schema.json")));
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "test/corpora/xml/xsd/corpus.json")));
        var checkedResult = schema.Evaluate(corpus.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true });
        if (!checkedResult.IsValid) throw new InvalidDataException(JsonSerializer.Serialize(checkedResult));
        Console.WriteLine($"Frozen xml-fixtures-1 schema: {corpus.RootElement.GetProperty("cases").GetArrayLength()} fixtures pass JsonSchema.Net9.4.0.");
        return 0;
    }
    var start = new ProcessStartInfo(python) { UseShellExecute = false, WorkingDirectory = root };
    start.ArgumentList.Add(Path.Combine(root, "tools/oracles/xml/xsd", name + ".py"));
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch pinned oracle script.");
    if (!process.WaitForExit(120_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); return 124; }
    return process.ExitCode;
}
