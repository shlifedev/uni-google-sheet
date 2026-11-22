using System.Reflection;
using System.Runtime.Loader;
using HolySheet.Core;

namespace HolySheet.Tooling;

/// <summary>Loads each project's rules for one run, so the desktop app and CLI watch see a rebuilt DLL immediately.</summary>
public sealed class ValidatorAssemblies : IDisposable
{
    private sealed class RuleContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var core = typeof(ITableValidator).Assembly;
            if (name.Name == core.GetName().Name) return core;
            string dependency = Path.Combine(directory, name.Name + ".dll");
            if (!File.Exists(dependency)) return null;
            using var stream = File.OpenRead(dependency);
            return LoadFromStream(stream);
        }
    }

    private readonly Dictionary<string, (RuleContext Context, Assembly Assembly)> _loaded = new();

    public Assembly Load(string path)
    {
        if (_loaded.TryGetValue(path, out var existing)) return existing.Assembly;
        using var file = File.OpenRead(path);
        var context = new RuleContext(Path.GetDirectoryName(path)!);
        try
        {
            Assembly assembly = context.LoadFromStream(file);
            _loaded.Add(path, (context, assembly));
            return assembly;
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _loaded.Values) entry.Context.Unload();
        _loaded.Clear();
    }
}
