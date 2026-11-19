#nullable enable
using System;

namespace HolySheet.Core
{
    internal static class Guard
    {
        public static T NotNull<T>(T? value, string name) where T : class
        {
            return value ?? throw new ArgumentNullException(name);
        }
    }
}
