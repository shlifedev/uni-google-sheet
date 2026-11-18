#nullable enable
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UGS
{
    /// <summary>
    /// Loads a table set: reads the manifest, checks it against the generated code, verifies every data file and decodes it.
    /// Either every table loads or an exception is thrown; a half-loaded set is never returned.
    /// </summary>
    public static class TableLoader
    {
        /// <param name="previous">A snapshot already in memory. Tables whose data hash is unchanged are carried over instead of decoded again.</param>
        public static TableSetData Load(TableSetDefinition definition, TableSource source, TableLoadOptions? options = null, ITableSet? previous = null)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            TableLoadOptions effective = options ?? TableLoadOptions.Default;
            string manifestFileName = effective.ManifestFileName ?? definition.ManifestFileName;
            byte[]? manifestBytes = source.Read(manifestFileName);
            TableManifest manifest = ReadManifest(definition, source, effective, manifestFileName, manifestBytes);
            Plan plan = new Plan(definition, manifest, previous);

            if (effective.Parallel && plan.PendingCount > 1)
            {
                ParallelOptions parallelOptions = new ParallelOptions();
                if (effective.MaxDegreeOfParallelism > 0)
                {
                    parallelOptions.MaxDegreeOfParallelism = effective.MaxDegreeOfParallelism;
                }

                try
                {
                    Parallel.For(0, plan.Tables.Length, parallelOptions, index =>
                    {
                        if (plan.Tables[index] == null)
                        {
                            TableManifestEntry entry = plan.Entries[index];
                            plan.Tables[index] = Decode(definition.Tables[index], entry, source, source.Read(entry.File), effective);
                        }
                    });
                }
                catch (AggregateException exception)
                {
                    throw Unwrap(exception);
                }
            }
            else
            {
                for (int index = 0; index < plan.Tables.Length; index++)
                {
                    if (plan.Tables[index] == null)
                    {
                        TableManifestEntry entry = plan.Entries[index];
                        plan.Tables[index] = Decode(definition.Tables[index], entry, source, source.Read(entry.File), effective);
                    }
                }
            }

            return new TableSetData(manifest, plan.Tables!, plan.Reused);
        }

        /// <summary>The same as <see cref="Load"/> without blocking on I/O. With <see cref="TableLoadOptions.Parallel"/>, decoding runs on the thread pool.</summary>
        public static async ValueTask<TableSetData> LoadAsync(TableSetDefinition definition, TableSource source, TableLoadOptions? options = null, ITableSet? previous = null, CancellationToken cancellationToken = default)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            TableLoadOptions effective = options ?? TableLoadOptions.Default;
            string manifestFileName = effective.ManifestFileName ?? definition.ManifestFileName;
            byte[]? manifestBytes = await source.ReadAsync(manifestFileName, cancellationToken).ConfigureAwait(false);
            TableManifest manifest = ReadManifest(definition, source, effective, manifestFileName, manifestBytes);
            Plan plan = new Plan(definition, manifest, previous);

            if (effective.Parallel && plan.PendingCount > 1)
            {
                int limit = effective.MaxDegreeOfParallelism > 0 ? effective.MaxDegreeOfParallelism : Environment.ProcessorCount;
                using (SemaphoreSlim gate = new SemaphoreSlim(limit))
                {
                    Task[] tasks = new Task[plan.PendingCount];
                    int taskIndex = 0;
                    for (int index = 0; index < plan.Tables.Length; index++)
                    {
                        if (plan.Tables[index] != null)
                        {
                            continue;
                        }

                        int tableIndex = index;
                        tasks[taskIndex++] = Task.Run(async () =>
                        {
                            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                TableManifestEntry entry = plan.Entries[tableIndex];
                                byte[]? bytes = await source.ReadAsync(entry.File, cancellationToken).ConfigureAwait(false);
                                plan.Tables[tableIndex] = Decode(definition.Tables[tableIndex], entry, source, bytes, effective);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }, cancellationToken);
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
            else
            {
                for (int index = 0; index < plan.Tables.Length; index++)
                {
                    if (plan.Tables[index] != null)
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    TableManifestEntry entry = plan.Entries[index];
                    byte[]? bytes = await source.ReadAsync(entry.File, cancellationToken).ConfigureAwait(false);
                    plan.Tables[index] = Decode(definition.Tables[index], entry, source, bytes, effective);
                }
            }

            return new TableSetData(manifest, plan.Tables!, plan.Reused);
        }

        /// <summary>Reads only the manifest. Use it to decide whether a reload or a patch download is needed at all.</summary>
        public static TableManifest ReadManifest(TableSetDefinition definition, TableSource source, TableLoadOptions? options = null)
        {
            TableLoadOptions effective = options ?? TableLoadOptions.Default;
            string manifestFileName = effective.ManifestFileName ?? definition.ManifestFileName;
            return ReadManifest(definition, source, effective, manifestFileName, source.Read(manifestFileName));
        }

        private static TableManifest ReadManifest(TableSetDefinition definition, TableSource source, TableLoadOptions options, string manifestFileName, byte[]? manifestBytes)
        {
            if (manifestBytes == null)
            {
                throw new TableLoadException(TableLoadError.FileMissing,
                    "The table manifest '" + manifestFileName + "' was not found in " + source.Description + ". Build the tables first, or check the data path.");
            }

            TableManifest manifest = TableManifest.Parse(DecodeUtf8(manifestBytes));
            if (manifest.Serializer != definition.Serializer)
            {
                throw new TableLoadException(TableLoadError.SchemaMismatch,
                    "The data was written with the '" + manifest.Serializer + "' serializer but the generated code reads '" + definition.Serializer + "'. Rebuild so the code and the data come from the same build.");
            }

            string? publicKey = options.PublicKey ?? definition.PublicKey;
            if (publicKey != null && !options.SkipSignature)
            {
                if (!manifest.IsSigned)
                {
                    throw new TableLoadException(TableLoadError.SignatureInvalid,
                        "The table manifest is not signed, but this build requires a signature. The data may have been replaced.");
                }

                if (!TableSignature.Verify(manifest, publicKey))
                {
                    throw new TableLoadException(TableLoadError.SignatureInvalid,
                        "The table manifest signature does not match the public key. The manifest was modified after it was built, or it was signed with a different key.");
                }
            }

            if (manifest.Schema != definition.SchemaHash)
            {
                throw new TableLoadException(TableLoadError.SchemaMismatch, DescribeSchemaMismatch(definition, manifest));
            }

            return manifest;
        }

        private static ITable Decode(TableDefinition table, TableManifestEntry entry, TableSource source, byte[]? bytes, TableLoadOptions options)
        {
            if (bytes == null)
            {
                throw new TableLoadException(TableLoadError.FileMissing,
                    "The data file '" + entry.File + "' listed in the manifest was not found in " + source.Description + ".", table.Name);
            }

            if (options.VerifyHashes)
            {
                // The size check is free and catches truncation before the more expensive hash.
                if (entry.Bytes >= 0 && bytes.Length != entry.Bytes)
                {
                    throw new TableLoadException(TableLoadError.HashMismatch,
                        "The data file '" + entry.File + "' is " + bytes.Length + " bytes but the manifest lists " + entry.Bytes + ". It was modified, truncated, or belongs to another build.", table.Name);
                }

                string hash = TableHash.Compute(bytes);
                if (hash != entry.Hash)
                {
                    throw new TableLoadException(TableLoadError.HashMismatch,
                        "The data file '" + entry.File + "' does not match the manifest (file " + hash + ", manifest " + entry.Hash + "). It was modified or belongs to another build.", table.Name);
                }
            }

            ITable result;
            try
            {
                result = table.Decode(bytes, entry);
            }
            catch (TableLoadException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new TableLoadException(TableLoadError.DecodeFailed,
                    "The data file '" + entry.File + "' could not be decoded: " + exception.Message, table.Name, exception);
            }

            if (result.Count != entry.Rows)
            {
                throw new TableLoadException(TableLoadError.DecodeFailed,
                    "The data file '" + entry.File + "' holds " + result.Count + " rows but the manifest lists " + entry.Rows + ".", table.Name);
            }

            return result;
        }

        private static string DescribeSchemaMismatch(TableSetDefinition definition, TableManifest manifest)
        {
            StringBuilder detail = new StringBuilder();
            ReadOnlyArray<TableDefinition> tables = definition.Tables;
            for (int index = 0; index < tables.Length; index++)
            {
                TableManifestEntry? entry = manifest.FindTable(tables[index].Name);
                if (entry == null)
                {
                    detail.Append(" '").Append(tables[index].Name).Append("' is missing from the data;");
                }
                else if (entry.Schema != tables[index].SchemaHash)
                {
                    detail.Append(" '").Append(tables[index].Name).Append("' has a different layout;");
                }
            }

            if (manifest.Tables.Count != tables.Length)
            {
                for (int index = 0; index < manifest.Tables.Count; index++)
                {
                    bool known = false;
                    for (int tableIndex = 0; tableIndex < tables.Length && !known; tableIndex++)
                    {
                        known = tables[tableIndex].Name == manifest.Tables[index].Name;
                    }

                    if (!known)
                    {
                        detail.Append(" '").Append(manifest.Tables[index].Name).Append("' is in the data but not in the code;");
                    }
                }
            }

            if (detail.Length == 0)
            {
                detail.Append(" an enum changed;");
            }

            return "The table data does not match the generated code (data schema " + manifest.Schema + ", code expects " + definition.SchemaHash + "):"
                + detail.ToString().TrimEnd(';') + ". Rebuild and ship the generated code and the data together.";
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            // Skip a byte order mark if some editor added one.
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }

        private static Exception Unwrap(AggregateException exception)
        {
            AggregateException flat = exception.Flatten();
            for (int index = 0; index < flat.InnerExceptions.Count; index++)
            {
                if (flat.InnerExceptions[index] is TableLoadException loadException)
                {
                    return loadException;
                }
            }

            return flat.InnerExceptions.Count > 0 ? flat.InnerExceptions[0] : exception;
        }

        // Decides, before any data file is touched, which tables must be decoded and which can be carried over.
        private sealed class Plan
        {
            public readonly ITable?[] Tables;
            public readonly TableManifestEntry[] Entries;
            public readonly bool[] Reused;
            public readonly int PendingCount;

            public Plan(TableSetDefinition definition, TableManifest manifest, ITableSet? previous)
            {
                ReadOnlyArray<TableDefinition> tables = definition.Tables;
                Tables = new ITable?[tables.Length];
                Entries = new TableManifestEntry[tables.Length];
                Reused = new bool[tables.Length];
                int pending = 0;
                for (int index = 0; index < tables.Length; index++)
                {
                    // The set-level schema already matched, so a missing or different table here means a corrupt manifest.
                    TableManifestEntry? entry = manifest.FindTable(tables[index].Name);
                    if (entry == null || entry.Schema != tables[index].SchemaHash)
                    {
                        throw new TableLoadException(TableLoadError.SchemaMismatch,
                            "The manifest's table '" + tables[index].Name + "' is missing or has a different layout than the generated code, although the overall schema hash matches. The manifest is inconsistent.", tables[index].Name);
                    }

                    Entries[index] = entry;
                    ITable? carried = FindReusable(previous, index, entry);
                    if (carried != null)
                    {
                        Tables[index] = carried;
                        Reused[index] = true;
                    }
                    else
                    {
                        pending++;
                    }
                }

                PendingCount = pending;
            }

            private static ITable? FindReusable(ITableSet? previous, int index, TableManifestEntry entry)
            {
                if (previous == null || index >= previous.Tables.Count)
                {
                    return null;
                }

                ITable candidate = previous.Tables[index];
                bool same = candidate.Name == entry.Name && candidate.Entry.Hash == entry.Hash && candidate.Entry.Schema == entry.Schema;
                return same ? candidate : null;
            }
        }
    }
}
