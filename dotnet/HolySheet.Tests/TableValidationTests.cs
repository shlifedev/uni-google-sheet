using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Sources;
using HolySheet.Tooling;
using Sample.Validators;

namespace HolySheet.Tests;

public sealed class TableValidationTests
{
    [Fact]
    public void Configured_dll_checks_partial_and_full_runs_and_reports_original_joined_cell()
    {
        using var temp = new TempDir();
        var config = new HolySheetConfig { BaseDirectory = temp.Path };
        string dll = typeof(ItemPriceValidator).Assembly.Location;
        File.Copy(dll, temp.Combine("Rules.dll"));
        config.ValidatorAssemblies.Add("Rules.dll");
        config = HolySheetConfig.Parse(config.ToJson(), temp.Path);
        BuildResult Read(string price, bool partial = false)
        {
            var book = new Workbook("Book", [
                Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n1,Potion"),
                Sheets.Of("Item.Col.Shop", "Key<int>,int\nId,Price\n,\n1," + price)]);
            using var assemblies = new ValidatorAssemblies();
            return TableBuild.Run(config, new TableBuildOptions
            {
                Sources = [new MemoryWorkbookSource(book)], LoadValidatorAssembly = assemblies.Load,
                Only = partial ? SheetSelection.Parse(["Book/Item.Col.Shop"]) : null
            });
        }
        var first = Read("10");
        Assert.True(first.Success, Sheets.Errors(first));
        TableBuild.Commit(config, first);
        var error = Read("-1", partial: true);
        Assert.False(error.Success);
        Assert.Null(error.Output);
        var diagnostic = Assert.Single(error.Diagnostics.Items);
        Assert.Equal("Book / Item.Col.Shop!B4", diagnostic.Location);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        var warning = Read("0");
        Assert.True(warning.Success, Sheets.Errors(warning));
        Assert.Equal(Severity.Warning, Assert.Single(warning.Diagnostics.Items).Severity);
        config.ValidatorAssemblies.Add("missing.dll");
        Assert.False(Read("10").Success);
    }
}
