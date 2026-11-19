using HolySheet.Core.Config;

namespace HolySheet.Tooling;

/// <summary>Starts a project: a holysheet.json with one local source and, optionally, a small sample workbook that builds.</summary>
public static class ProjectInit
{
    /// <summary>Whether <paramref name="directory"/> looks like the root of a Unity project.</summary>
    public static bool IsUnityProject(string directory) =>
        Directory.Exists(Path.Combine(directory, "Assets")) && Directory.Exists(Path.Combine(directory, "ProjectSettings"));

    /// <summary>
    /// Writes holysheet.json in <paramref name="directory"/> and creates its Sheets folder. Returns the config path.
    /// With <paramref name="unity"/> null, a Unity project is detected by its Assets and ProjectSettings folders.
    /// </summary>
    public static string Create(string directory, bool sample, bool? unity = null, bool force = false)
    {
        directory = Path.GetFullPath(directory);
        string path = Path.Combine(directory, HolySheetConfig.DefaultFileName);
        if (File.Exists(path) && !force)
        {
            throw new IOException($"'{path}' already exists.");
        }

        var config = new HolySheetConfig { BaseDirectory = directory };
        config.Sources.Add(new SourceConfig { Type = sample ? SourceType.Csv : SourceType.Xlsx, Path = "Sheets" });
        if (unity ?? IsUnityProject(directory))
        {
            // Generated code is compiled by Unity; data goes to StreamingAssets so it is a plain file the watcher can follow.
            config.Output.Code = "Assets/Game/Generated/Tables";
            config.Output.Data = "Assets/StreamingAssets/Tables";
        }

        Directory.CreateDirectory(Path.Combine(directory, "Sheets"));
        config.Save(path);
        if (sample)
        {
            foreach ((string name, string content) in SampleFiles)
            {
                File.WriteAllText(Path.Combine(directory, "Sheets", name), content);
            }
        }

        return path;
    }

    /// <summary>The same small workbook as 'holysheet init --sample': enums, a table with a key, references and a config table.</summary>
    public static readonly (string Name, string Content)[] SampleFiles =
    [
        ("Enums.csv", "byte,,Flags<int>,\nEItemKind,,EItemTag,\nKind of item,,Tags an item can carry,\nNone,,None,0\nWeapon,,Tradable,1 << 0\nArmor,,Stackable,1 << 1\nConsumable,,Quest,1 << 2\n"),
        ("Item.csv", "Key<int>,string,Enum<EItemKind>,Enum<EItemTag>,int,float[],#note\nId,Name,Kind,Tags,Price,Stats,\nItem id,Display name,Kind,Tags,Price in gold,Stat values,Designer notes\n1001,Wooden Sword,Weapon,Tradable,100,\"3, 1.5\",starter\n1002,Leather Armor,Armor,Tradable,150,5,\n2001,Potion,Consumable,\"Tradable, Stackable\",25,,\n~9999,Unfinished,Weapon,,0,,this row is skipped\n"),
        ("Monster.csv", "Key<int>,Mapping<EMonsterId>,string,int,Ref<Item>[],Ref<Item>?\nId,Code,Name,Hp,Drops,Rare\nMonster id,Name in code,Display name,Hit points,Items it can drop,Rare drop\n1,Slime,Slime,30,2001,\n2,Goblin,Goblin,80,\"1001, 2001\",1002\n"),
        ("Game.Config.csv", "int,MaxLevel,Highest level a character can reach,60\nfloat,GoldRate,Multiplier on gold drops,1.5\nstring,Version,Data version label,sample\n")
    ];
}
