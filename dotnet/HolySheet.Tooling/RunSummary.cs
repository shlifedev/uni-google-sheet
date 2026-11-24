using System.Globalization;
using HolySheet.Core;
using HolySheet.Core.Output;

namespace HolySheet.Tooling;

public static class RunSummary
{
    public static string Describe(BuildResult build, CommitReport? commit, string? failure)
    {
        if (failure != null)
        {
            return "Failed: " + failure;
        }

        if (!build.Success)
        {
            return build.Diagnostics.ErrorCount + " error(s), " + build.Diagnostics.WarningCount + " warning(s). Nothing was written.";
        }

        string summary = build.TableCount + " tables, " + build.RowCount + " rows, " + build.Enums.Count + " enums";
        if (build.Only != null)
        {
            summary += " (partial: " + build.RefreshedSheets.Count + " sheet" + (build.RefreshedSheets.Count == 1 ? "" : "s") + " read again)";
        }

        string timing = " in " + (build.ReadTime + build.BuildTime).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms (csv " + build.CsvBackend + ")";
        if (commit == null)
        {
            return "Checked " + summary + timing + ". No problems that stop a build.";
        }

        return commit.HasChanges
            ? "Built " + summary + timing + ": " + commit.Written.Count + " written, " + commit.Deleted.Count + " deleted."
            : "Up to date: " + summary + timing + ".";
    }
}
