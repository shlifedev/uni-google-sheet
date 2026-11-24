#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace HolySheet
{
    public static class TableHash
    {
        private const string HexDigits = "0123456789abcdef";

        public static string Compute(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

#if NET5_0_OR_GREATER
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            return ToHex(hash);
#else
            using (SHA256 sha256 = SHA256.Create())
            {
                return ToHex(sha256.ComputeHash(data));
            }
#endif
        }

        public static string Compute(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            return Compute(Encoding.UTF8.GetBytes(text));
        }

        public static string ToHex(ReadOnlySpan<byte> bytes)
        {
            char[] chars = new char[bytes.Length * 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                chars[index * 2] = HexDigits[bytes[index] >> 4];
                chars[index * 2 + 1] = HexDigits[bytes[index] & 0xF];
            }

            return new string(chars);
        }
    }
}
