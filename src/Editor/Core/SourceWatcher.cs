#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace UGS.Core
{
    /// <summary>
    /// Watches source files and folders and calls back once changes have settled.
    /// Saving a workbook produces a burst of file events (temporary file, rename, lock file); the debounce turns the burst into one call.
    /// </summary>
    public sealed class SourceWatcher : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly Timer _timer;
        private readonly TimeSpan _debounce;
        private readonly Action _onChanged;
        private volatile bool _disposed;

        public SourceWatcher(IEnumerable<string> paths, TimeSpan debounce, Action onChanged)
        {
            _debounce = debounce;
            _onChanged = Guard.NotNull(onChanged, nameof(onChanged));
            _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
                if (directory == null || !Directory.Exists(directory))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(directory)
                {
                    // A single file is watched through its folder, because editors replace files rather than write into them.
                    Filter = Directory.Exists(path) ? "*.*" : Path.GetFileName(path),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false
                };
                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += OnEvent;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }

        /// <summary>Number of folders being watched. Zero means there is nothing local to watch (all sources are remote).</summary>
        public int WatchedCount => _watchers.Count;

        private void OnEvent(object sender, FileSystemEventArgs args)
        {
            string name = Path.GetFileName(args.FullPath);
            string extension = Path.GetExtension(name);
            // Only sheets matter. Excel's "~$" lock files and editor temp files come and go without the data changing.
            bool relevant = (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                && !name.StartsWith("~$", StringComparison.Ordinal);
            if (!relevant || _disposed)
            {
                return;
            }

            try
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Fire()
        {
            if (!_disposed)
            {
                _onChanged();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _timer.Dispose();
        }
    }
}
