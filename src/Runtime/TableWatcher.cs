#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace HolySheet
{
    public enum TableWatchMode
    {
        Auto,

        Polling,

        /// <summary>Never check on its own. Call <see cref="TableWatcher{T}.CheckNow"/>, for example once per frame on platforms without threads.</summary>
        Manual
    }

    public sealed class TableWatchOptions
    {
        public TableWatchMode Mode { get; set; } = TableWatchMode.Auto;

        public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(150);

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

        public TableLoadOptions? LoadOptions { get; set; }

        public SynchronizationContext? SynchronizationContext { get; set; }

        public bool RaiseEventsInline { get; set; }
    }

    public sealed class TableReloadedEventArgs<T> where T : class, ITableSet
    {
        public T Previous { get; }
        public T Current { get; }

        public IReadOnlyList<string> ChangedTables { get; }

        public TableManifestDiff Diff { get; }

        public TableReloadedEventArgs(T previous, T current, IReadOnlyList<string> changedTables, TableManifestDiff diff)
        {
            Previous = previous;
            Current = current;
            ChangedTables = changedTables;
            Diff = diff;
        }
    }

    public delegate T TableSetLoader<T>(TableSource source, TableLoadOptions? options, T? previous) where T : class, ITableSet;

    public sealed class TableWatcher<T> : IDisposable where T : class, ITableSet
    {
        private const int MaxAttempts = 5;

        private readonly TableSetLoader<T> _loader;
        private readonly TableSource _source;
        private readonly TableWatchOptions _options;
        private readonly SynchronizationContext? _context;
        private readonly string _manifestPath;
        private readonly object _reloadLock = new object();
        private readonly Timer _timer;
        private FileSystemWatcher? _fileWatcher;
        private T _current;
        private string _stamp;
        private int _attempt;
        private bool _polling;
        private volatile bool _disposed;

        public T Current => Volatile.Read(ref _current);

        public string Directory { get; }

        public event Action<TableReloadedEventArgs<T>>? Reloaded;

        public event Action<Exception>? ReloadFailed;

        public TableWatcher(string directory, string manifestFileName, TableSetLoader<T> loader, TableWatchOptions? options = null)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("A directory is required.", nameof(directory));
            }

            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _options = options ?? new TableWatchOptions();
            _context = _options.SynchronizationContext ?? SynchronizationContext.Current;
            Directory = Path.GetFullPath(directory);
            string manifestName = _options.LoadOptions?.ManifestFileName ?? manifestFileName;
            _manifestPath = Path.Combine(Directory, manifestName);
            _source = new DirectoryTableSource(Directory);

            _stamp = ReadStamp();
            _current = _loader(_source, _options.LoadOptions, null);
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            Start();
        }

        public bool CheckNow()
        {
            if (_disposed)
            {
                return false;
            }

            return TryReload(raiseInline: true, out _);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FileSystemWatcher? fileWatcher = _fileWatcher;
            _fileWatcher = null;
            if (fileWatcher != null)
            {
                fileWatcher.EnableRaisingEvents = false;
                fileWatcher.Dispose();
            }

            _timer.Dispose();
        }

        private void Start()
        {
            if (_options.Mode == TableWatchMode.Manual)
            {
                return;
            }

            if (_options.Mode == TableWatchMode.Auto && TryStartFileWatcher())
            {
                return;
            }

            _polling = true;
            _timer.Change(_options.PollInterval, Timeout.InfiniteTimeSpan);
        }

        private bool TryStartFileWatcher()
        {
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(Directory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.Error += OnFileWatcherError;
                watcher.EnableRaisingEvents = true;
                _fileWatcher = watcher;
                return true;
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException || exception is NotSupportedException || exception is NotImplementedException
                || exception is IOException || exception is ArgumentException || exception is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _timer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnFileWatcherError(object sender, ErrorEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            _polling = true;
            try
            {
                _timer.Change(_options.PollInterval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnTimer(object? state)
        {
            if (_disposed)
            {
                return;
            }

            bool retry = false;
            try
            {
                TryReload(raiseInline: _options.RaiseEventsInline, out retry);
            }
            catch (Exception exception)
            {
                try
                {
                    ReloadFailed?.Invoke(exception);
                }
                catch (Exception)
                {
                }
            }

            if (_disposed)
            {
                return;
            }

            try
            {
                if (retry)
                {
                    _timer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
                }
                else if (_polling)
                {
                    _timer.Change(_options.PollInterval, Timeout.InfiniteTimeSpan);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private bool TryReload(bool raiseInline, out bool retry)
        {
            retry = false;
            TableReloadedEventArgs<T>? reloaded = null;
            Exception? failure = null;
            lock (_reloadLock)
            {
                string stamp = ReadStamp();
                if (stamp == _stamp && _attempt == 0)
                {
                    return false;
                }

                T previous = _current;
                try
                {
                    T next = _loader(_source, _options.LoadOptions, previous);
                    _stamp = stamp;
                    _attempt = 0;
                    if (next.Manifest.Content == previous.Manifest.Content && next.Manifest.Schema == previous.Manifest.Schema)
                    {
                        // The manifest was rewritten with identical data. Nothing to announce.
                        return false;
                    }

                    TableManifestDiff diff = TableManifest.Diff(previous.Manifest, next.Manifest);
                    List<string> changed = new List<string>(diff.Added);
                    changed.AddRange(diff.Changed);
                    Volatile.Write(ref _current, next);
                    reloaded = new TableReloadedEventArgs<T>(previous, next, changed, diff);
                }
                catch (Exception exception)
                {
                    bool transient = exception is IOException
                        || (exception is TableLoadException loadException && (loadException.Error == TableLoadError.HashMismatch || loadException.Error == TableLoadError.FileMissing || loadException.Error == TableLoadError.ManifestInvalid));
                    if (transient && _attempt < MaxAttempts && !raiseInline)
                    {
                        _attempt++;
                        retry = true;
                        return false;
                    }

                    _stamp = stamp;
                    _attempt = 0;
                    failure = exception;
                }
            }

            if (failure != null)
            {
                Raise(() => ReloadFailed?.Invoke(failure), raiseInline);
                return false;
            }

            Raise(() => Reloaded?.Invoke(reloaded!), raiseInline);
            return true;
        }

        private void Raise(Action action, bool inline)
        {
            if (inline || _context == null)
            {
                action();
                return;
            }

            _context.Post(_ => action(), null);
        }

        // Cheap fingerprint of the manifest file. The manifest is written last, so a new stamp means a complete build is on disk.
        private string ReadStamp()
        {
            try
            {
                FileInfo info = new FileInfo(_manifestPath);
                if (!info.Exists)
                {
                    return "";
                }

                return info.LastWriteTimeUtc.Ticks.ToString() + ":" + info.Length.ToString();
            }
            catch (IOException)
            {
                return "";
            }
        }
    }
}
