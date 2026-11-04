using System.Text.Json;
using Json.Schema;

namespace PaceLaps.Agent.Tests.Contracts;

/// <summary>The shared JSON schemas (copied into <c>contracts/</c>), loaded once with their cross-file references.</summary>
internal static class ContractSchemas
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "contracts");

    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(Load);

    public static string Example(string name) => File.ReadAllText(Path.Combine(Folder, "examples", name));

    /// <summary>Validates JSON text against a schema file; returns the error messages (empty when valid).</summary>
    public static IReadOnlyList<string> Errors(string schemaFile, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Schemas.Value[schemaFile].Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return result.IsValid
            ? []
            : (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .DefaultIfEmpty("invalid (no detail)")
                .ToList();
    }

    public static void AssertValid(string schemaFile, string json)
    {
        var errors = Errors(schemaFile, json);
        Assert.True(errors.Count == 0, $"{schemaFile} rejected the message:\n{string.Join("\n", errors)}\n{json}");
    }

    private static Dictionary<string, JsonSchema> Load()
    {
        var registry = new SchemaRegistry();
        var options = new BuildOptions { SchemaRegistry = registry };
        var result = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);

        // common first: the others reference it.
        var files = Directory.GetFiles(Folder, "*.schema.json")
            .OrderBy(f => Path.GetFileName(f) == "common.schema.json" ? 0 : 1)
            .ToList();
        foreach (var file in files)
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file), options);
            result[Path.GetFileName(file)] = schema;
        }

        return result;
    }
}
