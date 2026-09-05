using System.Collections.Generic;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Bounds how many loaded diffs are kept in memory.
    /// <para>
    /// Diffs are loaded lazily and then <b>never released</b>: each one hangs off its
    /// <see cref="ChangedFile"/>, which hangs off its commit, which lives in the loaded log. Browsing
    /// is therefore monotonic growth — measured at roughly 7 MB per 60 commits inspected (465 diffs,
    /// 21k lines). It is retention, not a leak: clearing the collections gives it all back, which is
    /// exactly what this does.
    /// </para>
    /// <para>
    /// Budgeted in <b>lines</b> rather than files, because that is what the memory actually tracks —
    /// a 20-line diff and a 5000-line diff are not the same thing to hold on to. Evicting only
    /// clears the parsed rows and flips <see cref="ChangedFile.DiffLoaded"/> back, so revisiting the
    /// file simply reloads it.
    /// </para>
    /// </summary>
    public sealed class DiffCache
    {
        /// <summary>
        /// Roughly 6–7 MB at the measured ~330 bytes per line (a unified line plus its share of the
        /// side-by-side rows). Comfortably more than a browsing session needs, and bounded.
        /// </summary>
        public const int DefaultMaxLines = 20_000;

        private readonly int _maxLines;
        private readonly LinkedList<ChangedFile> _order = new();   // most-recently-used at the front
        private readonly Dictionary<ChangedFile, LinkedListNode<ChangedFile>> _nodes =
            new(ReferenceEqualityComparer.Instance);

        public DiffCache(int maxLines = DefaultMaxLines) => _maxLines = maxLines;

        /// <summary>Lines currently held across every retained diff.</summary>
        public int RetainedLines { get; private set; }

        /// <summary>Number of files whose diff is currently retained.</summary>
        public int Count => _nodes.Count;

        /// <summary>
        /// Records a freshly loaded diff as most-recently-used, then evicts from the other end until
        /// the budget is met. The file just loaded is never evicted, even on its own — the user is
        /// looking at it, and dropping it would loop.
        /// </summary>
        public void Retain(ChangedFile file)
        {
            if (file == null)
                return;

            if (_nodes.TryGetValue(file, out LinkedListNode<ChangedFile>? existing))
            {
                RetainedLines -= existing.Value.Diff.Count;
                _order.Remove(existing);
            }

            LinkedListNode<ChangedFile> node = _order.AddFirst(file);
            _nodes[file] = node;
            RetainedLines += file.Diff.Count;

            EvictWhileOverBudget();
        }

        /// <summary>Drops everything, e.g. when the repository closes or the log is rebuilt.</summary>
        public void Clear()
        {
            foreach (ChangedFile file in _order)
                Release(file);
            _order.Clear();
            _nodes.Clear();
            RetainedLines = 0;
        }

        private void EvictWhileOverBudget()
        {
            // Stop at one entry: the newest is the one on screen.
            while (RetainedLines > _maxLines && _order.Count > 1)
            {
                LinkedListNode<ChangedFile>? oldest = _order.Last;
                if (oldest == null)
                    return;

                RetainedLines -= oldest.Value.Diff.Count;
                Release(oldest.Value);
                _nodes.Remove(oldest.Value);
                _order.RemoveLast();
            }
        }

        private static void Release(ChangedFile file)
        {
            file.Diff.Clear();
            file.Rows.Clear();
            // Back to "not loaded", so selecting it again re-reads it from git.
            file.DiffLoaded = false;
        }
    }
}
