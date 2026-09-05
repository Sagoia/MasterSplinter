using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // Diff presentation: unified vs side-by-side, whitespace, compare mode, binary previews.
    public sealed partial class MainViewModel
    {
        // ---- Diff view options (DIFF-002/003/004) ----------------------------------------------

        private DiffViewMode _diffViewMode = DiffViewMode.Unified;

        /// <summary>Toggles between unified and side-by-side diff layout (DIFF-002).</summary>
        public bool IsSideBySide
        {
            get => _diffViewMode == DiffViewMode.SideBySide;
            set
            {
                DiffViewMode mode = value ? DiffViewMode.SideBySide : DiffViewMode.Unified;
                if (_diffViewMode == mode) return;
                _diffViewMode = mode;
                OnPropertyChanged(nameof(IsSideBySide));
                EnsureSideBySideRows(SelectedFile);
                UpdateDiffViewState();
            }
        }

        /// <summary>
        /// Build the side-by-side rows for an already-loaded diff, if they are not built yet.
        /// <para>
        /// These used to be built on every diff load regardless of mode, which measured as 41% of
        /// the diff cache spent on a view the user may never open (157 bytes per row, on top of
        /// 203 per unified line). Building them here instead costs nothing until side-by-side is
        /// actually switched on, and it is a pure in-memory transform of the lines already loaded —
        /// no git, no I/O.
        /// </para>
        /// </summary>
        private void EnsureSideBySideRows(ChangedFile? file)
        {
            if (file == null || !file.DiffLoaded || file.IsBinary) return;
            if (_diffViewMode != DiffViewMode.SideBySide || file.Rows.Count > 0) return;

            foreach (DiffRow row in SideBySideBuilder.Build(file.Diff, DiffLanguages.IdForPath(file.Path)))
                file.Rows.Add(row);
        }

        private bool _syntaxHighlightOn = true;
        public bool SyntaxHighlightOn
        {
            get => _syntaxHighlightOn;
            set
            {
                if (!SetProperty(ref _syntaxHighlightOn, value)) return;
                SyntaxState.Enabled = value;       // read by the Syntax attached property (DIFF-003)
                ReloadCurrentDiff();               // re-realize the diff so colors refresh
            }
        }

        private WhitespaceMode _whitespace = WhitespaceMode.None;
        private int _whitespaceIndex;
        public int WhitespaceIndex
        {
            get => _whitespaceIndex;
            set
            {
                if (!SetProperty(ref _whitespaceIndex, value)) return;
                _whitespace = value switch
                {
                    1 => WhitespaceMode.IgnoreChange,
                    2 => WhitespaceMode.IgnoreAll,
                    _ => WhitespaceMode.None,
                };
                ReloadCurrentDiff();               // re-fetch the diff with the new whitespace flag
            }
        }

        private bool _showUnified, _showSideBySide, _showBinary;
        public bool ShowUnified { get => _showUnified; private set => SetProperty(ref _showUnified, value); }
        public bool ShowSideBySide { get => _showSideBySide; private set => SetProperty(ref _showSideBySide, value); }
        public bool ShowBinary { get => _showBinary; private set => SetProperty(ref _showBinary, value); }

        private string _changedSummary = "";
        public string ChangedSummary { get => _changedSummary; private set => SetProperty(ref _changedSummary, value); }

        private void UpdateDiffViewState()
        {
            ChangedFile? f = SelectedFile;
            bool hasFile = f != null;
            bool binary = hasFile && f!.IsBinary;
            ShowBinary = binary;
            ShowUnified = hasFile && !binary && _diffViewMode == DiffViewMode.Unified;
            ShowSideBySide = hasFile && !binary && _diffViewMode == DiffViewMode.SideBySide;
        }

        private void ReloadCurrentDiff()
        {
            ChangedFile? f = SelectedFile;
            if (f == null) return;
            f.DiffLoaded = false;
            _ = LoadDiffForAsync(f);
        }

        // ---- Compare two commits / refs (DIFF-006/007) -----------------------------------------

        private CommitRow? _markedCommit;
        private string? _compareBase;
        private string? _compareTarget;
        private List<string> _refNames = new();

        private bool _isCompareMode;
        public bool IsCompareMode
        {
            get => _isCompareMode;
            private set
            {
                if (SetProperty(ref _isCompareMode, value))
                {
                    OnPropertyChanged(nameof(IsSingleCommitMode));
                    OnPropertyChanged(nameof(ShowCommitPane));
                }
            }
        }
        public bool IsSingleCommitMode => !_isCompareMode;

        private string _compareTitle = "";
        public string CompareTitle { get => _compareTitle; private set => SetProperty(ref _compareTitle, value); }

        /// <summary>BR-007: "↑N ahead · ↓M behind" for the compared refs; empty when identical.</summary>
        private string _compareAheadBehind = "";
        public string CompareAheadBehind
        {
            get => _compareAheadBehind;
            private set { if (SetProperty(ref _compareAheadBehind, value)) OnPropertyChanged(nameof(HasCompareAheadBehind)); }
        }
        public bool HasCompareAheadBehind => _compareAheadBehind.Length > 0;

        public bool HasMarkedCommit => _markedCommit != null;

        /// <summary>Names of branches/tags/remotes + HEAD, for the compare-refs picker (DIFF-007).</summary>
        public IReadOnlyList<string> CompareRefNames => _refNames;

        public void MarkForComparison(CommitRow commit)
        {
            _markedCommit = commit;
            OnPropertyChanged(nameof(HasMarkedCommit));
        }

        public Task CompareWithMarkedAsync(CommitRow target)
        {
            if (_markedCommit == null) return Task.CompletedTask;
            return EnterCompareAsync(_markedCommit.FullHash, target.FullHash,
                $"{Short(_markedCommit.FullHash)} → {Short(target.FullHash)}");
        }

        public Task CompareRefsAsync(string a, string b) => EnterCompareAsync(a, b, $"{a} → {b}");

        private async Task EnterCompareAsync(string a, string b, string title)
        {
            if (_repo == null) return;
            GitRepository repo = _repo;

            IsCompareMode = true;
            _compareBase = a;
            _compareTarget = b;
            CompareTitle = title;
            SelectedFile = null;

            try
            {
                var files = await Task.Run(() => repo.ChangedFilesRange(a, b));
                DiffStat stat = await Task.Run(() => repo.RangeStat(a, b));
                // BR-007: divergence between the two refs, alongside the diff. Computed here so
                // comparing marked commits gets it too, not just the ref picker.
                var ab = await Task.Run(() => repo.AheadBehind(a, b));
                if (!IsCompareMode) return; // exited while loading
                CompareAheadBehind = ab is { Ahead: 0, Behind: 0 }
                    ? ""
                    : $"↑{ab.Ahead} ahead · ↓{ab.Behind} behind";
                PanelFiles.Clear();
                foreach (var f in files) PanelFiles.Add(f);
                ChangedSummary = FormatStat(stat);
                SelectedFile = PanelFiles.FirstOrDefault();
                UpdateDiffViewState();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        public void ExitCompare()
        {
            if (!IsCompareMode) return;
            ClearCompareState();
            CommitRow? commit = SelectedCommit;
            SelectedFile = null;
            PanelFiles.Clear();
            ChangedSummary = "";
            if (commit != null)
                _ = LoadFilesForAsync(commit);
            UpdateDiffViewState();
        }

        private void ClearCompareState()
        {
            IsCompareMode = false;
            _compareBase = null;
            _compareTarget = null;
            CompareTitle = "";
            CompareAheadBehind = "";
        }

        // ---- Binary / image diff (DIFF-005) ----------------------------------------------------

        private string _binaryInfoText = "";
        public string BinaryInfoText { get => _binaryInfoText; private set => SetProperty(ref _binaryInfoText, value); }

        private ImageSource? _binaryOldImage;
        public ImageSource? BinaryOldImage { get => _binaryOldImage; private set => SetProperty(ref _binaryOldImage, value); }

        private ImageSource? _binaryNewImage;
        public ImageSource? BinaryNewImage { get => _binaryNewImage; private set => SetProperty(ref _binaryNewImage, value); }

        private bool _binaryHasImages;
        public bool BinaryHasImages { get => _binaryHasImages; private set => SetProperty(ref _binaryHasImages, value); }

        private async Task LoadBinaryPreviewAsync(ChangedFile file, string baseRef, string targetRef, bool range)
        {
            BinaryOldImage = null;
            BinaryNewImage = null;
            BinaryHasImages = false;
            BinaryInfoText = $"Binary file — {file.Path}";

            if (_repo == null) return;
            GitRepository repo = _repo;

            string ext = Path.GetExtension(file.Path).ToLowerInvariant();
            bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";
            if (!isImage) return;

            string newRef = targetRef;                          // new side = target commit/ref
            string oldRef = range ? baseRef : targetRef + "^";  // old side = base, or first parent

            try
            {
                byte[] newBytes = file.Status == FileChangeStatus.Deleted
                    ? Array.Empty<byte>()
                    : await Task.Run(() => repo.FileBytesAt(newRef, file.Path));
                byte[] oldBytes = file.Status == FileChangeStatus.Added
                    ? Array.Empty<byte>()
                    : await Task.Run(() => repo.FileBytesAt(oldRef, file.Path));

                if (!ReferenceEquals(file, SelectedFile)) return;

                BinaryNewImage = await BytesToImageAsync(newBytes);
                BinaryOldImage = await BytesToImageAsync(oldBytes);
                BinaryHasImages = BinaryNewImage != null || BinaryOldImage != null;
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        private static async Task<ImageSource?> BytesToImageAsync(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                return image;
            }
            catch { return null; }
        }

        private static string FormatStat(DiffStat s)
        {
            if (s.IsEmpty) return "No changes";
            string files = $"{s.Files} file{(s.Files == 1 ? "" : "s")} changed";
            return $"{files},  +{s.Insertions}  −{s.Deletions}";
        }

        private static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;
    }
}
