#nullable enable
// Types the C# compiler looks for that netstandard2.1 (and Unity's API profiles) do not have.
// Internal, so they never clash with the same types in another assembly.

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets the compiler emit init-only setters and records.</summary>
    internal static class IsExternalInit
    {
    }
}
