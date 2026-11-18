using System.Runtime.CompilerServices;

namespace UGS.Core;

internal static class Guard
{
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null) where T : class
    {
        return value ?? throw new ArgumentNullException(name);
    }
}
