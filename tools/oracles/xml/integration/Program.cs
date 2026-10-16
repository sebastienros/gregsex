using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

if (args.Length != 0) throw new ArgumentException("Fixed no-argument procedure; run from repository root.");
using var definition = JsonDocument.Parse(File.ReadAllText("test/GregsEx.Tests/Contracts/Xml/XmlFixtures.schema.json"));
var options = new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true };
if (!MetaSchemas.Draft202012.Evaluate(definition.RootElement, options).IsValid) throw new InvalidDataException("Invalid frozen schema.");
var schema = JsonSchema.FromText(definition.RootElement.GetRawText(), new BuildOptions(), new Uri("https://gregsex.local/xml-fixtures.schema.json"));
var manifest = JsonNode.Parse(File.ReadAllText("test/corpora/xml/manifest.json"))!;
var combined = new JsonObject { ["contractVersion"] = manifest["contractVersion"]!.DeepClone(), ["dataId"] = manifest["dataId"]!.DeepClone() };
var cases = new JsonArray();
var ids = new HashSet<string>(StringComparer.Ordinal);
foreach (var lane in manifest["lanes"]!.AsArray())
{
    var corpus = JsonNode.Parse(File.ReadAllText(lane!["corpus"]!.GetValue<string>()))!;
    if (corpus["contractVersion"]!.GetValue<string>() != combined["contractVersion"]!.GetValue<string>() ||
        corpus["dataId"]!.GetValue<string>() != combined["dataId"]!.GetValue<string>()) throw new InvalidDataException("Mixed identities.");
    foreach (var recipe in corpus["cases"]!.AsArray())
    {
        if (!ids.Add(recipe!["id"]!.GetValue<string>())) throw new InvalidDataException("Duplicate joined recipe.");
        cases.Add(recipe.DeepClone());
    }
}
combined["cases"] = cases;
using var instance = JsonDocument.Parse(combined.ToJsonString());
var result = schema.Evaluate(instance.RootElement, options);
if (!result.IsValid) { Console.Error.WriteLine(JsonSerializer.Serialize(result)); return 1; }
if (cases.Count != manifest["fixtureCount"]!.GetValue<int>()) throw new InvalidDataException("Incomplete join.");
Console.WriteLine($"Draft2020-12 meta-schema and complete joined xml-fixtures-1: {cases.Count} valid original recipes.");
return 0;
