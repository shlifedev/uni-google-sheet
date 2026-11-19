using System.Diagnostics;
using Sample.MemoryPackData;
using HolySheet;

// Usage: Sample.Server <dataDirectory> [--parallel] [--watch]
//
//   dotnet run --project dotnet/samples/Sample.Server -- dotnet/samples/GameData/Data/MemoryPack --watch
//
// With --watch it keeps running and reloads when the data changes: edit the sheet, run `holysheet build` (or leave `holysheet watch` running)
// and the new values appear here without a restart.

if (args.Length == 0 || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("usage: Sample.Server <dataDirectory> [--parallel] [--watch]");
    return 2;
}

string directory = args[0];
var options = new TableLoadOptions { Parallel = args.Contains("--parallel") };

try
{
    if (!args.Contains("--watch"))
    {
        var stopwatch = Stopwatch.StartNew();
        GameTables tables = GameTables.Load(directory, options);
        stopwatch.Stop();
        Console.WriteLine($"loaded {tables.Tables.Count} tables in {stopwatch.Elapsed.TotalMilliseconds:F1} ms ({(options.Parallel ? "parallel" : "sequential")})");
        Print(tables);
        return 0;
    }

    // RaiseEventsInline: a console app has no main-thread context to post to, so events run on the thread that noticed the change.
    using TableWatcher<GameTables> watcher = GameTables.Watch(directory, new TableWatchOptions { LoadOptions = options, RaiseEventsInline = true });
    Print(watcher.Current);
    watcher.Reloaded += change =>
    {
        Console.WriteLine($"\nreloaded: {string.Join(", ", change.ChangedTables)} changed (content {change.Current.Manifest.Content[..12]})");
        Print(change.Current);
    };
    watcher.ReloadFailed += exception => Console.WriteLine($"\nreload failed, keeping the current tables: {exception.Message}");

    Console.WriteLine($"\nwatching {watcher.Directory}. Press Enter to stop.");
    Console.ReadLine();
    return 0;
}
catch (TableLoadException exception)
{
    Console.Error.WriteLine($"could not load tables ({exception.Error}): {exception.Message}");
    return 1;
}

static void Print(GameTables tables)
{
    Console.WriteLine($"{tables.Game.Value.Title}: max level {tables.Game.Value.MaxLevel}, gold rate {tables.Game.Value.GoldRate}");
    foreach (MonsterRow monster in tables.Monster.Rows)
    {
        // A reference holds a key; the table it points at resolves it.
        ItemRow weapon = tables.Item.Get(monster.Weapon);
        string drops = string.Join(", ", monster.Drops.Select(id => tables.Item.Get(id).Name.Replace('\n', ' ')));
        string rare = tables.Item.Find(monster.Rare)?.Name ?? "-";
        Console.WriteLine($"  {(EMonsterId)monster.Id,-7} hp {monster.Hp,5}  weapon {weapon.Name} (+{weapon.Attack})  drops [{drops}]  rare {rare}");
    }

    Console.WriteLine($"  tier 1 items: {string.Join(", ", tables.Item.ListByTier(1).Select(item => item.Code))}");
}
