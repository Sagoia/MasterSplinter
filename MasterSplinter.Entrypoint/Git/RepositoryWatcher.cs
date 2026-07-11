using System;
using System.IO;
using Microsoft.UI.Dispatching;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Watches a repository folder and raises a debounced callback on the UI thread when the
    /// working tree or the repository state changes (STATUS-004). Events under .git are dropped
    /// except for the files that signal a real state change (index, HEAD, refs, ...), so the
    /// app's own read-only git calls (which use --no-optional-locks) never re-trigger it.
    /// </summary>
    public sealed class RepositoryWatcher : IDisposable
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

        private readonly FileSystemWatcher _watcher;
        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _timer;
        private readonly Action<bool> _changed; // arg: true when .git state changed (branch/index)
        private readonly object _gate = new();
        private bool _worktreeDirty;
        private bool _repoDirty;
        private bool _disposed;

        /// <summary>Returns null when the folder cannot be watched (network drive, permissions…).</summary>
        public static RepositoryWatcher? TryCreate(string root, DispatcherQueue queue, Action<bool> changed)
        {
            try { return new RepositoryWatcher(root, queue, changed); }
            catch { return null; } // degrade to manual refresh
        }

        private RepositoryWatcher(string root, DispatcherQueue queue, Action<bool> changed)
        {
            _queue = queue;
            _changed = changed;

            _timer = queue.CreateTimer();
            _timer.Interval = Debounce;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Fire();

            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024, // event storms (branch switch) overflow 8 KB fast
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }

        private void OnEvent(object sender, FileSystemEventArgs e) => Classify(e.Name);

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Classify(e.OldName);
            Classify(e.Name);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            // Buffer overflow: events were lost, so assume everything changed.
            lock (_gate)
            {
                _worktreeDirty = true;
                _repoDirty = true;
            }
            RestartTimer();
        }

        // Runs on a threadpool thread. Only flag-setting happens here; git work is done by the
        // callback on the UI thread after the debounce.
        private void Classify(string? relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return;
            string p = relativePath.Replace('/', '\\');

            bool repo = false;
            if (p.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase) ||
                p.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                if (p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                    return; // in-flight git operations; the final write triggers separately
                string inGit = p.Length > 5 ? p[5..] : "";
                bool significant = inGit.Equals("index", StringComparison.OrdinalIgnoreCase)
                    || inGit.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                    || inGit.Equals("packed-refs", StringComparison.OrdinalIgnoreCase)
                    || inGit.Equals("MERGE_HEAD", StringComparison.OrdinalIgnoreCase)
                    || inGit.Equals("ORIG_HEAD", StringComparison.OrdinalIgnoreCase)
                    || inGit.StartsWith("refs\\", StringComparison.OrdinalIgnoreCase);
                if (!significant)
                    return; // objects, logs, hooks, ... — noise
                repo = true;
            }

            lock (_gate)
            {
                if (repo) _repoDirty = true;
                else _worktreeDirty = true;
            }
            RestartTimer();
        }

        private void RestartTimer()
        {
            // The timer belongs to the UI thread; restarting it there also serializes with Fire().
            _queue.TryEnqueue(() =>
            {
                if (_disposed) return;
                _timer.Stop();
                _timer.Start();
            });
        }

        private void Fire()
        {
            bool worktree, repo;
            lock (_gate)
            {
                worktree = _worktreeDirty;
                repo = _repoDirty;
                _worktreeDirty = false;
                _repoDirty = false;
            }
            if (!_disposed && (worktree || repo))
                _changed(repo);
        }

        public void Dispose()
        {
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _queue.TryEnqueue(() => _timer.Stop());
        }
    }
}
