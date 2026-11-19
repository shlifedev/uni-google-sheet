using System.Globalization;
using HolySheet.Core;
using HolySheet.Core.Output;

namespace HolySheet.Tooling;

/// <summary>One line describing a run, the same sentence the Unity window shows in its status tooltip.</summary>
public static class RunSummary
{
    /// <param name="commit">The commit report, or null when the run only checked or the build had errors.</param>
    /// <param name="failure">Set when the run itself failed (unreadable config, cannot write).</param>
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
