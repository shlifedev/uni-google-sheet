using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;
using HolySheet.Core.Output;
using HolySheet.Core.Sources;

namespace HolySheet.Tests;

internal static class Repo
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("The repository root (the folder with global.json) was not found.");
    });

    public static string Root => RootPath.Value;

    public static string SampleData => Path.Combine(Root, "dotnet", "samples", "GameData");

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "holysheet-tests", Guid.NewGuid().ToString("N"));

    public TempDir()
    {
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Sheets
{
    public static Sheet Of(string name, string csv, string workbook = "Book") =>
        Sheet.FromGrid(workbook, name, BuiltinCsvParser.Instance.Parse(csv.Replace("\r\n", "\n")));

    public static HolySheetConfig Config(string serializer = "memorypack") => new()
    {
        BaseDirectory = System.IO.Path.GetTempPath(),
        Codegen = { Serializer = serializer, Namespace = "Test.Data", TablesClass = "GameTables" },
        Parallel = false
    };

    public static BuildResult Build(params Sheet[] sheets) => Build(Config(), sheets);

    public static BuildResult Build(HolySheetConfig config, params Sheet[] sheets)
    {
        var workbook = new Workbook("Book", sheets);
        return TableBuild.Run(config, new TableBuildOptions { Sources = [new MemoryWorkbookSource(workbook)] });
    }

    public static string Errors(BuildResult result) => string.Join("\n", result.Diagnostics.Items.Select(item => item.ToString()));
}

internal sealed class SampleBuild : IDisposable
{
    private readonly TempDir _temp = new();

    public HolySheetConfig Config { get; }

    public BuildResult Result { get; }

    public CommitReport Report { get; }

    public string DataDirectory => Config.Resolve(Config.Output.Data);

    public string CodeDirectory => Config.Resolve(Config.Output.Code);

    public SampleBuild(string serializer, Action<HolySheetConfig>? configure = null)
    {
        Config = Load(serializer, _temp.Path);
        configure?.Invoke(Config);
        Result = TableBuild.Run(Config);
        Assert.True(Result.Success, Sheets.Errors(Result));
        Report = TableBuild.Commit(Config, Result);
    }

    public static HolySheetConfig Load(string serializer, string outputRoot)
    {
        HolySheetConfig config = HolySheetConfig.Load(Path.Combine(Repo.SampleData, $"holysheet.{serializer}.json"));
        config.Output.Code = Path.Combine(outputRoot, "code");
        config.Output.Data = Path.Combine(outputRoot, "data");
        config.Output.Csv = null;
        config.CacheDirectory = Path.Combine(outputRoot, SheetCache.DefaultDirectory);
        return config;
    }

    public CommitReport Rebuild(Action<HolySheetConfig>? configure = null, TableBuildOptions? options = null)
    {
        configure?.Invoke(Config);
        BuildResult result = TableBuild.Run(Config, options);
        Assert.True(result.Success, Sheets.Errors(result));
        return TableBuild.Commit(Config, result);
    }

    public void Dispose() => _temp.Dispose();
}
