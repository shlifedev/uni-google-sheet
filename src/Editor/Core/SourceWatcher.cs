#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HolySheet.Core
{
    /// <summary>
    /// Watches source files and folders and calls back once changes have settled.
    /// Saving a workbook produces a burst of file events (temporary file, rename, lock file); the debounce turns the burst into one call.
    /// </summary>
    public sealed class SourceWatcher : IDisposable
    {
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
        private readonly Timer _timer;
        private readonly TimeSpan _debounce;
        private readonly Action _onChanged;
        private volatile bool _disposed;

        public SourceWatcher(IEnumerable<string> paths, TimeSpan debounce, Action onChanged)
        {
            _debounce = debounce;
            _onChanged = Guard.NotNull(onChanged, nameof(onChanged));
            _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            SetPaths(paths);
        }

        /// <summary>Keep subscriptions for unchanged paths and start watching newly configured sources.</summary>
        public void SetPaths(IEnumerable<string> paths)
        {
            var wanted = new HashSet<string>(paths.Select(Path.GetFullPath), _watchers.Comparer);
            lock (_watchers)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SourceWatcher));
                foreach (string path in wanted)
                {
                    bool folder = Directory.Exists(path);
                    if (_watchers.TryGetValue(path, out var existing))
                    {
                        if ((existing.Filter == "*") == folder) continue;
                        existing.Dispose();
                        _watchers.Remove(path);
                    }

                    string? directory = folder ? path : Path.GetDirectoryName(path);
                    if (directory == null || !Directory.Exists(directory)) continue;
                    var watcher = new FileSystemWatcher(directory)
                    {
                        Filter = folder ? "*" : Path.GetFileName(path),
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false
                    };

                    // For an explicit path the watcher's filter already matched the file, including custom config filenames.
                    FileSystemEventHandler handler = folder ? OnSheetEvent : OnEvent;
                    watcher.Changed += handler;
                    watcher.Created += handler;
                    watcher.Deleted += handler;
                    watcher.Renamed += folder ? OnSheetEvent : OnEvent;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(path, watcher);
                }

                foreach (string removed in _watchers.Keys.Where(key => !wanted.Contains(key)).ToArray())
                {
                    _watchers[removed].Dispose();
                    _watchers.Remove(removed);
                }
            }
        }

        public int WatchedCount
        {
            get { lock (_watchers) return _watchers.Count; }
        }

        private static bool IsSheet(string path)
        {
            string name = Path.GetFileName(path);
            string extension = Path.GetExtension(name);
            return !name.StartsWith("~$", StringComparison.Ordinal) &&
                (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase));
        }

        private void OnSheetEvent(object sender, FileSystemEventArgs args)
        {
            if (IsSheet(args.FullPath) || (args is RenamedEventArgs renamed && IsSheet(renamed.OldFullPath)))
                OnEvent(sender, args);
        }

        private void OnEvent(object sender, FileSystemEventArgs args)
        {
            if (_disposed) return;
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
            lock (_watchers)
            {
                _disposed = true;
                foreach (FileSystemWatcher watcher in _watchers.Values)
                    watcher.Dispose();
                _watchers.Clear();
            }

            _timer.Dispose();
        }
    }
}
