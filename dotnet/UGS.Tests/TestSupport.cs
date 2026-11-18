using UGS.Core;
using UGS.Core.Config;
using UGS.Core.Csv;
using UGS.Core.Output;
using UGS.Core.Sources;

namespace UGS.Tests;

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

/// <summary>A folder under the system temp directory that is removed when the test ends.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ugs-tests", Guid.NewGuid().ToString("N"));

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
    /// <summary>A sheet written as CSV text, which is the shortest way to spell one in a test.</summary>
    public static Sheet Of(string name, string csv, string workbook = "Book") =>
        Sheet.FromGrid(workbook, name, BuiltinCsvParser.Instance.Parse(csv.Replace("\r\n", "\n")));

    public static UgsConfig Config(string serializer = "memorypack") => new()
    {
        BaseDirectory = System.IO.Path.GetTempPath(),
        Codegen = { Serializer = serializer, Namespace = "Test.Data", TablesClass = "GameTables" },
        Parallel = false
    };

    /// <summary>Builds the given sheets with the default rules, without touching the disk.</summary>
    public static BuildResult Build(params Sheet[] sheets) => Build(Config(), sheets);

    public static BuildResult Build(UgsConfig config, params Sheet[] sheets)
    {
        var workbook = new Workbook("Book", sheets);
        return TableBuild.Run(config, new TableBuildOptions { Sources = [new MemoryWorkbookSource(workbook)] });
    }

    public static string Errors(BuildResult result) => string.Join("\n", result.Diagnostics.Items.Select(item => item.ToString()));
}

/// <summary>The sample workbook, built into a temp folder with the given serializer.</summary>
internal sealed class SampleBuild : IDisposable
{
    private readonly TempDir _temp = new();

    public UgsConfig Config { get; }

    public BuildResult Result { get; }

    public CommitReport Report { get; }

    public string DataDirectory => Config.Resolve(Config.Output.Data);

    public string CodeDirectory => Config.Resolve(Config.Output.Code);

    public SampleBuild(string serializer, Action<UgsConfig>? configure = null)
    {
        Config = Load(serializer, _temp.Path);
        configure?.Invoke(Config);
        Result = TableBuild.Run(Config);
        Assert.True(Result.Success, Sheets.Errors(Result));
        Report = TableBuild.Commit(Config, Result);
    }

    public static UgsConfig Load(string serializer, string outputRoot)
    {
        UgsConfig config = UgsConfig.Load(Path.Combine(Repo.SampleData, $"ugs.{serializer}.json"));
        config.Output.Code = Path.Combine(outputRoot, "code");
        config.Output.Data = Path.Combine(outputRoot, "data");
        config.Output.Csv = null;
        return config;
    }

    /// <summary>Builds again into the same folders, as the watch command would after a sheet edit.</summary>
    public CommitReport Rebuild(Action<UgsConfig>? configure = null, TableBuildOptions? options = null)
    {
        configure?.Invoke(Config);
        BuildResult result = TableBuild.Run(Config, options);
        Assert.True(result.Success, Sheets.Errors(result));
        return TableBuild.Commit(Config, result);
    }

    public void Dispose() => _temp.Dispose();
}
