#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;

// Kept stable across releases: the optional UGS.Csv.Sep DLL is compiled against this identity,
// and Unity would otherwise version this assembly as 0.0.0.0.
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: InternalsVisibleTo("UGS.Tests")]
