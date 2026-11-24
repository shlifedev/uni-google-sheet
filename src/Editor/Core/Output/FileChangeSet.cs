#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace HolySheet.Core.Output
{
    internal sealed class FileChangeSet
    {
        private readonly List<Change> changes = new();
        private readonly HashSet<string> destinations = new(StringComparer.Ordinal);
        private readonly CommitReport report = new();
        private readonly string conflictHint;

        public FileChangeSet(string conflictHint) => this.conflictHint = conflictHint;

        public CommitReport Apply()
        {
            if (changes.Count == 0)
            {
                return report;
            }

            int applied = 0;
            try
            {
                foreach (Change change in changes)
                {
                    if (change.Original != null)
                    {
                        if (!File.ReadAllBytes(change.Path).AsSpan().SequenceEqual(change.Original))
                        {
                            throw new IOException($"'{change.Path}' changed before it could be written.");
                        }

                        applied++;
                    }

                    if (change.Content == null)
                    {
                        File.Delete(change.Path);
                        if (File.Exists(change.Path + ".meta"))
                        {
                            change.OriginalMeta = File.ReadAllBytes(change.Path + ".meta");
                            File.Delete(change.Path + ".meta");
                        }

                        report.Deleted.Add(change.Name);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                        WriteAtomically(change.Path, change.Content, replace: change.Original != null);
                        if (change.Original == null)
                        {
                            applied++;
                        }

                        report.Written.Add(change.Name);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                for (int index = applied - 1; index >= 0; index--)
                {
                    Change change = changes[index];
                    try
                    {
                        if (change.Original == null)
                        {
                            File.Delete(change.Path);
                        }
                        else
                        {
                            File.WriteAllBytes(change.Path, change.Original);
                        }

                        if (change.OriginalMeta != null)
                        {
                            File.WriteAllBytes(change.Path + ".meta", change.OriginalMeta);
                        }
                    }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                throw new IOException($"The output could not be written ({exception.Message}). The files already changed were restored.", exception);
            }

            return report;
        }

        public void PlanWrite(string path, byte[] content, string name, bool canReplace)
        {
            path = AddDestination(path);
            byte[]? original = null;
            if (File.Exists(path))
            {
                if (!canReplace)
                {
                    throw new IOException($"'{path}' already exists. {conflictHint}");
                }

                original = File.ReadAllBytes(path);
                if (original.AsSpan().SequenceEqual(content))
                {
                    report.Unchanged++;
                    return;
                }
            }

            changes.Add(new Change(path, name, content, original));
        }

        public void PlanDelete(string path, string name)
        {
            path = AddDestination(path);
            changes.Add(new Change(path, name, null, File.ReadAllBytes(path)));
        }

        private string AddDestination(string path)
        {
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                throw new IOException($"'{path}' is a directory. A file is expected here.");
            }

            for (string? parent = Path.GetDirectoryName(path); parent != null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
            {
                if (File.Exists(parent))
                {
                    throw new IOException($"'{parent}' is a file. An output directory is expected here.");
                }
            }

            if (!destinations.Add(path))
            {
                throw new IOException($"More than one output uses '{path}'. Choose distinct output paths.");
            }

            return path;
        }

        private static void WriteAtomically(string path, byte[] content, bool replace)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N").Substring(0, 12) + ".holysheet-tmp";
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (stream)
                {
                    stream.Write(content, 0, content.Length);
                }

                if (replace)
                {
                    try
                    {
                        File.Replace(temporary, path, null);
                        return;
                    }
                    catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
                    {
                        if (!File.Exists(temporary))
                        {
                            throw;
                        }

                        File.Delete(path);
                    }
                }

                File.Move(temporary, path);
            }
            catch
            {
                File.Delete(temporary);
                throw;
            }
        }

        private sealed class Change
        {
            public Change(string path, string name, byte[]? content, byte[]? original)
            {
                Path = path;
                Name = name;
                Content = content;
                Original = original;
            }

            public string Path { get; }

            public string Name { get; }

            public byte[]? Content { get; }

            public byte[]? Original { get; }

            public byte[]? OriginalMeta { get; set; }
        }
    }
}
