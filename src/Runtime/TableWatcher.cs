#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace HolySheet
{
    /// <summary>How a <see cref="TableWatcher{T}"/> notices that the data changed.</summary>
    public enum TableWatchMode
    {
        /// <summary>File system notifications where the platform has them, otherwise polling.</summary>
        Auto,

        /// <summary>Check the manifest on a timer. Works everywhere that has timers.</summary>
        Polling,

        /// <summary>Never check on its own. Call <see cref="TableWatcher{T}.CheckNow"/>, for example once per frame on platforms without threads.</summary>
        Manual
    }

    public sealed class TableWatchOptions
    {
        public TableWatchMode Mode { get; set; } = TableWatchMode.Auto;

        /// <summary>How long the data must stay quiet before reloading. The tool writes several files per build; this waits for the last one.</summary>
        public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(150);

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

        public TableLoadOptions? LoadOptions { get; set; }

        /// <summary>
        /// Where events are raised. Null means "the context current when the watcher was created":
        /// in Unity that is the main thread, on a plain server it is the thread pool.
        /// </summary>
        public SynchronizationContext? SynchronizationContext { get; set; }

        /// <summary>Raise events on the thread that noticed the change instead of posting them to a context.</summary>
        public bool RaiseEventsInline { get; set; }
    }

    /// <summary>What a reload changed.</summary>
    public sealed class TableReloadedEventArgs<T> where T : class, ITableSet
    {
        public T Previous { get; }
        public T Current { get; }

        /// <summary>Names of the tables whose data was decoded again. Tables not listed are the very same objects as before.</summary>
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

    /// <summary>Loads a snapshot from a source; <paramref name="previous"/> is null for the first load.</summary>
    public delegate T TableSetLoader<T>(TableSource source, TableLoadOptions? options, T? previous) where T : class, ITableSet;

    /// <summary>
    /// Keeps a table set up to date with a directory of data files.
    /// <see cref="Current"/> always returns a complete snapshot: a reload builds the new one on the side and swaps it in with one
    /// reference write, so readers never see a mix of old and new tables. A failed reload keeps the old snapshot.
    /// </summary>
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
        private bool _reportedTransientFailure;
        private bool _polling;
        private volatile bool _disposed;

        /// <summary>The latest successfully loaded snapshot. Safe to read from any thread.</summary>
        public T Current => Volatile.Read(ref _current);

        public string Directory { get; }

        /// <summary>Raised after a new snapshot became <see cref="Current"/>.</summary>
        public event Action<TableReloadedEventArgs<T>>? Reloaded;

        /// <summary>Raised when the data changed but could not be loaded. <see cref="Current"/> is unchanged.</summary>
        public event Action<Exception>? ReloadFailed;

        /// <summary>Loads the tables from <paramref name="directory"/> once (throwing if that fails) and then watches for changes.</summary>
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

        /// <summary>
        /// Checks for changed data right now, on the calling thread, and reloads if needed.
        /// Returns true when a new snapshot was installed. Errors are reported through <see cref="ReloadFailed"/>.
        /// </summary>
        public bool CheckNow()
        {
            if (_disposed)
            {
                return false;
            }

            return TryReload(manual: true, out _);
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

            // Restarting the timer on every event is the debounce: the reload runs once the directory has been quiet.
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
            // The notification buffer overflowed or the directory went away. Fall back to polling so changes are not lost.
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
                TryReload(manual: false, out retry);
            }
            catch (Exception exception)
            {
                // A handler threw while running inline on this timer thread. An unhandled exception here would end the process,
                // so hand it to ReloadFailed instead; if that handler throws as well there is nowhere left to report it.
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

        private bool TryReload(bool manual, out bool retry)
        {
            retry = false;
            bool raiseInline = manual || _options.RaiseEventsInline;
            TableReloadedEventArgs<T>? reloaded = null;
            Exception? failure = null;
            lock (_reloadLock)
            {
                string stamp = ReadStamp();
                if (stamp != _stamp)
                {
                    _stamp = stamp;
                    _attempt = 0;
                    _reportedTransientFailure = false;
                }
                else if (_attempt == 0 && !_reportedTransientFailure)
                {
                    return false;
                }

                T previous = _current;
                try
                {
                    T next = _loader(_source, _options.LoadOptions, previous);
                    _attempt = 0;
                    _reportedTransientFailure = false;
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
                    // A hash mismatch or a missing file usually means the tool is still writing. Try again a few times before reporting.
                    bool transient = exception is IOException
                        || (exception is TableLoadException loadException && (loadException.Error == TableLoadError.HashMismatch || loadException.Error == TableLoadError.FileMissing || loadException.Error == TableLoadError.ManifestInvalid));
                    // Data files can be repaired without another manifest write. Keep checking a reported
                    // transient failure on subsequent polls or file events, but report it once per manifest.
                    if (transient && _reportedTransientFailure)
                    {
                        return false;
                    }

                    if (transient && !manual && _attempt < MaxAttempts)
                    {
                        _attempt++;
                        retry = true;
                        return false;
                    }

                    _reportedTransientFailure = transient;
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
