using System.Text.Json;
using Json.Schema;
if (args.Length != 0) throw new ArgumentException("Fixed no-argument development procedure; run from repository root.");
using var definition = JsonDocument.Parse(File.ReadAllText("test/GregsEx.Tests/Contracts/Xml/XmlFixtures.schema.json"));
var options = new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true };
if (!MetaSchemas.Draft202012.Evaluate(definition.RootElement, options).IsValid) throw new InvalidDataException("Invalid frozen schema.");
var schema = JsonSchema.FromText(definition.RootElement.GetRawText(), new BuildOptions(), new Uri("https://gregsex.local/xml-fixtures.schema.json"));
using var instance = JsonDocument.Parse(File.ReadAllText("test/corpora/xml/xpath/Corpus.json"));
var result = schema.Evaluate(instance.RootElement, options);
if (!result.IsValid) { Console.Error.WriteLine(JsonSerializer.Serialize(result)); return 1; }
Console.WriteLine($"Draft2020-12 meta-schema and frozen xml-fixtures-1: {instance.RootElement.GetProperty("cases").GetArrayLength()} fixtures valid.");
return 0;
