using System.Text.Json.Nodes;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Output;
using HolySheet.Tooling;

namespace HolySheet.Desktop.Host;

internal static class Json
{
    public static JsonArray Strings(IEnumerable<string> items) => new(items.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());

    public static JsonArray Array(IEnumerable<JsonNode?> items) => new(items.ToArray());

    /// <summary>A copy without the properties set to null: the config parser treats a missing key as "not set".</summary>
    public static JsonObject WithoutNulls(JsonObject source)
    {
        var copy = new JsonObject();
        foreach ((string key, JsonNode? value) in source)
        {
            switch (value)
            {
                case null:
                    continue;
                case JsonObject child:
                    copy[key] = WithoutNulls(child);
                    break;
                case JsonArray array:
                    copy[key] = new JsonArray(array.Select(item => item is JsonObject element ? WithoutNulls(element) : item?.DeepClone()).ToArray());
                    break;
                default:
                    copy[key] = value.DeepClone();
                    break;
            }
        }

        return copy;
    }

    /// <summary>The name holysheet.json uses for a source type.</summary>
    public static string SourceTypeName(SourceType type) => type switch
    {
        SourceType.Xlsx => "xlsx",
        SourceType.Csv => "csv",
        SourceType.Google => "google",
        SourceType.GoogleFolder => "googleFolder",
        _ => type.ToString()
    };
}

/// <summary>How a run and its problems are written for the app. Diagnostics have the same shape as <c>holysheet build --json</c>.</summary>
internal static class RunJson
{
    public static JsonObject Describe(RunRecord record)
    {
        BuildResult build = record.Build;
        CommitReport? commit = record.Commit;
        string status = record.Failure != null ? "failed"
            : !build.Success ? "errors"
            : commit == null ? "checked"
            : commit.HasChanges ? "built"
            : "upToDate";
        return new JsonObject
        {
            ["status"] = status,
            ["success"] = record.Failure == null && build.Success,
            ["committed"] = record.Committed,
            ["failure"] = record.Failure,
            ["summary"] = RunSummary.Describe(build, commit, record.Failure),
            ["errors"] = build.Diagnostics.ErrorCount + (record.Failure != null ? 1 : 0),
            ["warnings"] = build.Diagnostics.WarningCount,
            ["tables"] = build.TableCount,
            ["rows"] = build.RowCount,
            ["enums"] = build.Enums.Count,
            ["partial"] = build.Only != null,
            ["only"] = build.Only?.ToString(),
            ["refreshed"] = Json.Strings(build.RefreshedSheets),
            ["csvBackend"] = build.CsvBackend,
            ["readMs"] = (long)build.ReadTime.TotalMilliseconds,
            ["buildMs"] = (long)build.BuildTime.TotalMilliseconds,
            ["written"] = Json.Strings(commit?.Written ?? []),
            ["deleted"] = Json.Strings(commit?.Deleted ?? []),
            ["unchanged"] = commit?.Unchanged ?? 0,
            ["finishedAt"] = record.FinishedAt.ToString("O"),
            // A run that failed outright has one problem: why. The window shows it the same way.
            ["diagnostics"] = Json.Array(record.Failure != null
                ? [Diagnostic(new Diagnostic(Severity.Error, "holysheet", null, record.Failure, ""))]
                : build.Diagnostics.Items.Where(item => item.Severity == Severity.Error).Concat(build.Diagnostics.Items.Where(item => item.Severity != Severity.Error)).Select(Diagnostic))
        };
    }

    public static JsonNode Diagnostic(Diagnostic diagnostic)
    {
        var item = new JsonObject
        {
            ["severity"] = diagnostic.Severity == Severity.Error ? "error" : "warning",
            ["location"] = diagnostic.Location,
            ["message"] = diagnostic.Message,
            ["hint"] = diagnostic.Hint,
            ["text"] = diagnostic.ToString()
        };
        if (diagnostic.Origin is { } origin)
        {
            item["workbook"] = origin.Workbook;
            item["sheet"] = origin.Sheet;
            item["cell"] = origin.A1;
            item["row"] = origin.Row;
            item["column"] = origin.Column;
        }

        return item;
    }
}
