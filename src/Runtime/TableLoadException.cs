#nullable enable
using System;

namespace HolySheet
{
    public enum TableLoadError
    {
        FileMissing,

        ManifestInvalid,

        SchemaMismatch,

        HashMismatch,

        SignatureInvalid,

        DecodeFailed
    }

    public sealed class TableLoadException : Exception
    {
        public TableLoadError Error { get; }

        public string? TableName { get; }

        public TableLoadException(TableLoadError error, string message, string? tableName = null, Exception? innerException = null)
            : base(message, innerException)
        {
            Error = error;
            TableName = tableName;
        }
    }
}
