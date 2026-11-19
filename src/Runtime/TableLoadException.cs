#nullable enable
using System;

namespace HolySheet
{
    /// <summary>Why a table set could not be loaded. Callers can branch on this instead of matching message text.</summary>
    public enum TableLoadError
    {
        /// <summary>A file the manifest lists, or the manifest itself, could not be read.</summary>
        FileMissing,

        /// <summary>The manifest text is not a manifest this runtime understands.</summary>
        ManifestInvalid,

        /// <summary>The data was built for a different shape of generated code. Rebuild and ship code and data together.</summary>
        SchemaMismatch,

        /// <summary>A data file does not match the hash in the manifest: it was modified, truncated or is from another build.</summary>
        HashMismatch,

        /// <summary>The manifest signature is missing or does not verify against the public key.</summary>
        SignatureInvalid,

        /// <summary>The bytes passed the hash check but the serializer could not decode them.</summary>
        DecodeFailed
    }

    /// <summary>Thrown when table data cannot be loaded. Nothing is partially applied: the previous tables stay as they were.</summary>
    public sealed class TableLoadException : Exception
    {
        public TableLoadError Error { get; }

        /// <summary>The table the problem is about, or null when it concerns the whole set.</summary>
        public string? TableName { get; }

        public TableLoadException(TableLoadError error, string message, string? tableName = null, Exception? innerException = null)
            : base(message, innerException)
        {
            Error = error;
            TableName = tableName;
        }
    }
}
