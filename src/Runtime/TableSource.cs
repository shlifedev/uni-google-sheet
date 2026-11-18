#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UGS
{
    /// <summary>
    /// Where the manifest and data files come from. The loader only ever asks for a file by its name,
    /// so the same generated code reads from disk on a server, from StreamingAssets in a player, or from a downloaded patch.
    /// </summary>
    public abstract class TableSource
    {
        /// <summary>Returns the file's bytes, or null when there is no such file.</summary>
        public abstract byte[]? Read(string fileName);

        /// <summary>Asynchronous read. The default runs <see cref="Read"/> inline; override it where blocking I/O is not allowed.</summary>
        public virtual ValueTask<byte[]?> ReadAsync(string fileName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<byte[]?>(Read(fileName));
        }

        /// <summary>A short description for error messages, such as the directory path.</summary>
        public virtual string Description => GetType().Name;

        public static TableSource FromDirectory(string directory)
        {
            return new DirectoryTableSource(directory);
        }

        public static TableSource FromDelegate(Func<string, byte[]?> read, string description = "delegate")
        {
            return new DelegateTableSource(read, description);
        }

        public static TableSource FromMemory(IReadOnlyDictionary<string, byte[]> files)
        {
            return new MemoryTableSource(files);
        }

        /// <summary>
        /// Reads the manifest and every data file it lists into memory, and returns a source that serves them from there.
        /// Use it to separate reading from decoding: read on the one thread a source allows (Unity's Resources must be read on
        /// the main thread), then load from the result with <see cref="TableLoadOptions.Parallel"/>. Files that are missing are
        /// simply absent from the result; the load that follows reports them.
        /// </summary>
        public static MemoryTableSource Preload(TableSource source, string manifestFileName = TableManifest.DefaultFileName)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[]? manifestBytes = source.Read(manifestFileName);
            if (manifestBytes == null)
            {
                return new MemoryTableSource(files);
            }

            files[manifestFileName] = manifestBytes;
            TableManifest manifest;
            try
            {
                manifest = TableManifest.Parse(System.Text.Encoding.UTF8.GetString(manifestBytes));
            }
            catch (TableLoadException)
            {
                // The load that follows parses it again and reports the problem properly.
                return new MemoryTableSource(files);
            }

            for (int index = 0; index < manifest.Tables.Count; index++)
            {
                string file = manifest.Tables[index].File;
                byte[]? bytes = source.Read(file);
                if (bytes != null)
                {
                    files[file] = bytes;
                }
            }

            return new MemoryTableSource(files);
        }
    }

    /// <summary>Reads files from a directory on disk.</summary>
    public sealed class DirectoryTableSource : TableSource
    {
        public string Directory { get; }

        public override string Description => Directory;

        public DirectoryTableSource(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("A directory is required.", nameof(directory));
            }

            Directory = directory;
        }

        public override byte[]? Read(string fileName)
        {
            string path = Path.Combine(Directory, fileName);
            try
            {
                // FileShare.ReadWrite: the build tool may be replacing files while a watcher reads them.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] bytes = new byte[stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0)
                        {
                            break;
                        }

                        offset += read;
                    }

                    if (offset != bytes.Length)
                    {
                        Array.Resize(ref bytes, offset);
                    }

                    return bytes;
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        public override async ValueTask<byte[]?> ReadAsync(string fileName, CancellationToken cancellationToken)
        {
            string path = Path.Combine(Directory, fileName);
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
                {
                    byte[] bytes = new byte[stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        offset += read;
                    }

                    if (offset != bytes.Length)
                    {
                        Array.Resize(ref bytes, offset);
                    }

                    return bytes;
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>Reads files through a callback. Use it for asset bundles, Addressables, embedded resources or a patch cache.</summary>
    public sealed class DelegateTableSource : TableSource
    {
        private readonly Func<string, byte[]?> _read;

        public override string Description { get; }

        public DelegateTableSource(Func<string, byte[]?> read, string description = "delegate")
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            Description = description;
        }

        public override byte[]? Read(string fileName)
        {
            return _read(fileName);
        }
    }

    /// <summary>Serves files that are already in memory.</summary>
    public sealed class MemoryTableSource : TableSource
    {
        private readonly IReadOnlyDictionary<string, byte[]> _files;

        public override string Description => "memory";

        /// <summary>Names of the files held.</summary>
        public IEnumerable<string> FileNames => _files.Keys;

        public MemoryTableSource(IReadOnlyDictionary<string, byte[]> files)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
        }

        public override byte[]? Read(string fileName)
        {
            return _files.TryGetValue(fileName, out byte[]? bytes) ? bytes : null;
        }
    }
}
