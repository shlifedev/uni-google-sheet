using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using MemoryPack;
using MessagePack;
using HolySheet.Core.Output;
using Mem = Sample.MemoryPackData;
using Msg = Sample.MessagePackData;

namespace HolySheet.Tests;

public class EndToEndTests
{
    private static readonly DateTime Released = new(2026, 1, 31, 13, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void The_checked_in_generated_code_is_what_the_tool_generates_now()
    {
        foreach ((string serializer, string project) in new[] { ("memorypack", "Sample.Tables.MemoryPack"), ("messagepack", "Sample.Tables.MessagePack") })
        {
            using var build = new SampleBuild(serializer);
            string checkedIn = Path.Combine(Repo.Root, "dotnet", "samples", project, "Generated");
            Assert.Equal(build.Result.Output!.CodeFiles.Keys, Directory.GetFiles(checkedIn, "*.g.cs").Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
            foreach (KeyValuePair<string, string> file in build.Result.Output.CodeFiles)
            {
                Assert.True(file.Value == File.ReadAllText(Path.Combine(checkedIn, file.Key)),
                    $"{project}/Generated/{file.Key} is stale. Run: holysheet build -c dotnet/samples/GameData/holysheet.{serializer}.json");
            }
        }
    }

    [Fact]
    public void MemoryPack_tables_load_with_every_value_intact()
    {
        using var build = new SampleBuild("memorypack");
        Mem.GameTables tables = Mem.GameTables.Load(build.DataDirectory);

        Assert.Equal(6, tables.Item.Count);
        Mem.ItemRow sword = tables.Item.Get(1001);
        Assert.Equal("wooden_sword", sword.Code);
        Assert.Equal("Wooden Sword", sword.Name);
        Assert.Equal(Mem.EItemKind.Weapon, sword.Kind);
        Assert.Equal(Mem.EItemTag.Tradable, sword.Tags);
        Assert.Equal(1, sword.Tier);
        Assert.Equal(100, sword.Price);
        Assert.Equal(2.5f, sword.Weight);
        Assert.Equal(0.25, sword.DropRate);
        Assert.True(sword.Tradable);
        Assert.Equal(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), sword.ReleasedAt);
        Assert.Equal(DateTimeKind.Utc, sword.ReleasedAt.Kind);
        Assert.Equal([3f, 1.5f], sword.Stats.ToArray());
        Assert.Equal(["sword", "blade"], sword.Aliases.ToArray());
        Assert.Equal(10L, sword.Exp);
        Assert.Equal([1, 2], sword.Sockets.ToArray());
        Assert.Equal(Mem.ERarity.Common, sword.Rarity);
        Assert.Equal(12, sword.Attack);
        Assert.Equal(0, sword.Defense);

        Mem.ItemRow armor = tables.Item.Get(1002);
        Assert.Equal(Released, armor.ReleasedAt);
        Assert.Equal(4f, armor.Weight);
        Assert.Equal([3], armor.Sockets.ToArray());
        Assert.True(armor.Aliases.IsEmpty);
        Assert.Equal(8, armor.Defense);

        Mem.ItemRow potion = tables.Item.Get(2001);
        Assert.Equal("포션 Potion", potion.Name);
        Assert.Equal(Mem.EItemTag.Tradable | Mem.EItemTag.Stackable, potion.Tags);
        Assert.Equal(["heal", "hp"], potion.Aliases.ToArray());
        Assert.True(potion.Stats.IsEmpty);
        Assert.Equal(0, potion.Attack);

        Mem.ItemRow ore = tables.Item.Get(3001);
        Assert.Equal("Ore \"raw\", heavy\nsecond line", ore.Name);
        Assert.Equal((Mem.EItemKind)10, ore.Kind);
        Assert.Equal(Mem.EItemKind.Material, ore.Kind);
        Assert.Equal(Mem.EItemTag.Sellable, ore.Tags);
        Assert.Equal(9007199254740991L, ore.Exp);
        Assert.Equal(default, ore.ReleasedAt);

        Assert.False(tables.Item.Contains(2002));
        Assert.Equal("Dragon Blade", tables.Item.Get(9001).Name);
        Assert.Equal(250, tables.Item.Get(9001).Attack);
        Assert.Equal([1001, 1002, 2001, 3001, 9001, 9002], tables.Item.Rows.Select(row => row.Id));

        Assert.Same(potion, tables.Item[2001]);
        Assert.Same(potion, tables.Item.GetByCode("potion"));
        Assert.Null(tables.Item.Find(42));
        Assert.Null(tables.Item.FindByCode("nothing"));
        Assert.False(tables.Item.TryGet(42, out _));
        Assert.True(tables.Item.TryGetByCode("iron_ore", out Mem.ItemRow found));
        Assert.Same(ore, found);
        Assert.Throws<KeyNotFoundException>(() => tables.Item.Get(42));
        Assert.Equal([1001, 1002, 2001], tables.Item.ListByTier(1).Select(row => row.Id));
        Assert.Equal([9001, 9002], tables.Item.ListByTier(3).Select(row => row.Id));
        Assert.True(tables.Item.ListByTier(99).IsEmpty);

        Mem.MonsterRow slime = tables.Monster.Get(1);
        Mem.MonsterRow goblin = tables.Monster.Get(2);
        Mem.MonsterRow dragon = tables.Monster.Get(3);
        Assert.False(slime.Rare.HasValue);
        Assert.Null(tables.Item.Find(slime.Rare));
        Assert.Throws<InvalidOperationException>(() => tables.Item.Get(slime.Rare));
        Assert.True(goblin.Rare.HasValue);
        Assert.Same(armor, tables.Item.Get(goblin.Rare));
        Assert.Same(sword, tables.Item.Get(goblin.Weapon));
        Assert.Equal([1001, 2001], goblin.Drops.ToArray());
        Assert.Equal(["Dragon Blade", "Phoenix Mail", "Ore \"raw\", heavy\nsecond line"], dragon.Drops.Select(id => tables.Item.Get(id).Name));
        Assert.True(slime.Weak.IsEmpty);
        Assert.Equal([Mem.EItemKind.Weapon, Mem.EItemKind.Consumable], dragon.Weak.ToArray());
        Assert.Equal([1, 2], tables.Monster.ListByZone("meadow").Select(row => row.Id));
        Assert.Equal(2, (int)Mem.EMonsterId.Goblin);
        Assert.Same(dragon, tables.Monster.Get((int)Mem.EMonsterId.Dragon));

        Assert.Equal("시작", tables.Text.Get("ui.start").Ko);
        Assert.Equal("", tables.Text.Get("ui.empty").En);
        Assert.Equal(60, tables.Game.Value.MaxLevel);
        Assert.Equal(1.5f, tables.Game.Value.GoldRate);
        Assert.Equal("HolySheet Sample", tables.Game.Value.Title);
        Assert.Same(sword, tables.Item.Get(tables.Game.Value.StartItem));
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), tables.Game.Value.OpensAt);
        Assert.False(tables.Game.Value.Maintenance);

        AssertLimits(tables.Limits.Get(1), tables.Limits.Get(2), tables.Limits.Get(3));
    }

    private static void AssertLimits(Mem.LimitsRow low, Mem.LimitsRow high, Mem.LimitsRow odd)
    {
        Assert.Equal((sbyte.MinValue, byte.MinValue, short.MinValue, ushort.MinValue), (low.I8, low.U8, low.I16, low.U16));
        Assert.Equal((int.MinValue, uint.MinValue, long.MinValue, ulong.MinValue), (low.I32, low.U32, low.I64, low.U64));
        Assert.Equal((float.MinValue, double.MinValue, false, ""), (low.F32, low.F64, low.Flag, low.Text));
        Assert.True(low.Ints.IsEmpty && low.Dates.IsEmpty && low.Bytes.IsEmpty);

        Assert.Equal((sbyte.MaxValue, byte.MaxValue, short.MaxValue, ushort.MaxValue), (high.I8, high.U8, high.I16, high.U16));
        Assert.Equal((int.MaxValue, uint.MaxValue, long.MaxValue, ulong.MaxValue), (high.I32, high.U32, high.I64, high.U64));
        Assert.Equal((float.MaxValue, double.MaxValue, true, " padded "), (high.F32, high.F64, high.Flag, high.Text));
        Assert.Equal([1, -1, 0], high.Ints.ToArray());
        Assert.Equal([9007199254740993L, -5L], high.Longs.ToArray());
        Assert.Equal([true, false, true, false], high.Flags.ToArray());
        Assert.Equal([(byte)0, (byte)255, (byte)16], high.Bytes.ToArray());
        Assert.Equal([new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc), new DateTime(2500, 1, 1, 0, 0, 0, DateTimeKind.Utc)], high.Dates.ToArray());
        Assert.Equal([0.1, 1e-7], high.Doubles.ToArray());

        Assert.Equal(((sbyte)-33, (byte)128, (short)-129, (ushort)256), (odd.I8, odd.U8, odd.I16, odd.U16));
        Assert.Equal((-32769, 65536u, -2147483649L, 4294967296ul), (odd.I32, odd.U32, odd.I64, odd.U64));
        Assert.Equal((0.1f, 0.30000000000000004, true), (odd.F32, odd.F64, odd.Flag));
        Assert.Equal("multi\nline \"quoted\", comma", odd.Text);
    }

    [Fact]
    public void MessagePack_tables_hold_exactly_the_same_values_as_MemoryPack_tables()
    {
        using var memoryPack = new SampleBuild("memorypack");
        using var messagePack = new SampleBuild("messagepack");
        string expected = Dump(Mem.GameTables.Load(memoryPack.DataDirectory));
        string actual = Dump(Msg.GameTables.Load(messagePack.DataDirectory));
        Assert.Equal(expected, actual);
        Assert.Contains("포션 Potion", expected);
        Assert.Contains("9223372036854775807", expected);
        Assert.Contains("18446744073709551615", expected);
        Assert.True(expected.Split('\n').Length > 150);
    }

    [Fact]
    public void The_tool_writes_the_same_bytes_as_the_MemoryPack_library()
    {
        using var build = new SampleBuild("memorypack");
        Mem.GameTables tables = Mem.GameTables.Load(build.DataDirectory);
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Item.bytes")), MemoryPackSerializer.Serialize(tables.Item.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Monster.bytes")), MemoryPackSerializer.Serialize(tables.Monster.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Text.bytes")), MemoryPackSerializer.Serialize(tables.Text.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Game.bytes")), MemoryPackSerializer.Serialize(tables.Game.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Limits.bytes")), MemoryPackSerializer.Serialize(tables.Limits.Rows.ToArray()));
    }

    [Fact]
    public void The_tool_writes_the_same_bytes_as_the_MessagePack_library()
    {
        using var build = new SampleBuild("messagepack");
        Msg.GameTables tables = Msg.GameTables.Load(build.DataDirectory);
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Item.bytes")), MessagePackSerializer.Serialize(tables.Item.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Monster.bytes")), MessagePackSerializer.Serialize(tables.Monster.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Text.bytes")), MessagePackSerializer.Serialize(tables.Text.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Game.bytes")), MessagePackSerializer.Serialize(tables.Game.Rows.ToArray()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(build.DataDirectory, "Limits.bytes")), MessagePackSerializer.Serialize(tables.Limits.Rows.ToArray()));
    }

    [Fact]
    public void An_empty_table_loads_as_an_empty_table()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("Sheets", "Csv"));
        File.Copy(Path.Combine(Repo.SampleData, "Sheets", "Game.xlsx"), temp.Combine("Sheets", "Game.xlsx"));
        string[] limits = File.ReadAllLines(Path.Combine(Repo.SampleData, "Sheets", "Csv", "Limits.csv"));
        File.WriteAllLines(temp.Combine("Sheets", "Csv", "Limits.csv"), limits.Take(3));
        foreach (string serializer in new[] { "memorypack", "messagepack" })
        {
            var config = SampleBuild.Load(serializer, temp.Combine(serializer));
            config.BaseDirectory = temp.Path;
            var result = HolySheet.Core.TableBuild.Run(config);
            Assert.True(result.Success, Sheets.Errors(result));
            HolySheet.Core.TableBuild.Commit(config, result);
            int count = serializer == "memorypack" ? Mem.GameTables.Load(config.Resolve(config.Output.Data)).Limits.Count : Msg.GameTables.Load(config.Resolve(config.Output.Data)).Limits.Count;
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public void Rows_and_tables_expose_nothing_that_can_change_them()
    {
        foreach (Assembly assembly in new[] { typeof(Mem.GameTables).Assembly, typeof(Msg.GameTables).Assembly })
        {
            foreach (Type type in assembly.GetExportedTypes().Where(type => type.Name.EndsWith("Row") || type.Name.EndsWith("Table") || type.Name == "GameTables"))
            {
                Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
                Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.True(property.SetMethod == null, $"{type.Name}.{property.Name} has a setter");
                    Type propertyType = property.PropertyType;
                    Assert.False(propertyType.IsArray, $"{type.Name}.{property.Name} exposes an array");
                    Assert.False(typeof(IList).IsAssignableFrom(propertyType) || typeof(IDictionary).IsAssignableFrom(propertyType), $"{type.Name}.{property.Name} exposes a mutable collection");
                }
            }
        }
    }

    [Fact]
    public void A_read_only_array_cannot_reach_the_table_memory()
    {
        using var build = new SampleBuild("memorypack");
        Mem.GameTables tables = Mem.GameTables.Load(build.DataDirectory);
        Mem.ItemRow sword = tables.Item.Get(1001);

        float[] copy = sword.Stats.ToArray();
        copy[0] = 999f;
        Assert.Equal(3f, sword.Stats[0]);

        Mem.ItemRow[] rows = tables.Item.Rows.ToArray();
        rows[0] = rows[1];
        Assert.Equal(1001, tables.Item.Rows[0].Id);

        Assert.IsAssignableFrom<IReadOnlyList<float>>(sword.Stats);
        Assert.False(typeof(IList<float>).IsAssignableFrom(typeof(ReadOnlyArray<float>)));
        Assert.False(typeof(ICollection<float>).IsAssignableFrom(typeof(ReadOnlyArray<float>)));
        Assert.Throws<ArgumentOutOfRangeException>(() => sword.Stats[2]);
        Assert.Equal(2, sword.Stats.AsSpan().Length);
    }

    private static string Dump(ITableSet tables)
    {
        var builder = new StringBuilder();
        foreach (ITable table in tables.Tables)
        {
            builder.Append("# ").Append(table.Name).Append(' ').Append(table.Count).Append('\n');
            var rows = (IEnumerable)table.GetType().GetProperty("Rows")!.GetValue(table)!;
            foreach (object row in rows)
            {
                foreach (PropertyInfo property in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(property => property.MetadataToken))
                {
                    builder.Append(property.Name).Append('=').Append(Format(property.GetValue(row))).Append('\n');
                }
            }
        }

        return builder.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture) + "/" + date.Kind,
        IEnumerable items => "[" + string.Join(",", items.Cast<object>().Select(Format)) + "]",
        _ when value.GetType().Name.EndsWith("Ref") => value.ToString()!,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!
    };
}
