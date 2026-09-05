using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;
using MasterSplinter.Entrypoint.ViewModels;

namespace MasterSplinter.Entrypoint.Controls
{
    public sealed partial class RepositoryWorkspace : UserControl
    {
        public MainViewModel Vm { get; } = new();

        /// <summary>Raised by the home screen's "Open Repository…" button (the window owns the picker).</summary>
        public event EventHandler? OpenRepositoryRequested;

        /// <summary>Raised when a recent repository is chosen on the home screen (carries its path).</summary>
        public event EventHandler<string>? OpenRecentRequested;

        // True while this view is pushing the VM's mode into the SelectorBar, so the
        // SelectionChanged handler doesn't bounce the change back into the VM.
        private bool _syncingModeBar;

        public RepositoryWorkspace()
        {
            InitializeComponent();
            DataContext = Vm;

            // The grouped working-copy view: a CollectionViewSource resource can't bind to the
            // DataContext, so its Source is wired here.
            StatusGroupsView.Source = Vm.StatusGroups;

            // Keep the bottom mode tabs in sync when the mode changes from elsewhere
            // (sidebar "Working Copy" tap, selecting a commit, refresh).
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsWorkingCopyMode) ||
                    e.PropertyName == nameof(MainViewModel.IsReflogMode))
                {
                    SyncModeBar();
                }
                // A blame window holds its own GitRepository, so it has to go when the workspace
                // moves to a different one (or to none) — otherwise it keeps showing a file from a
                // repository the user has closed.
                else if (e.PropertyName == nameof(MainViewModel.Repository))
                {
                    CloseBlameWindows();
                }
            };
        }

        private void SyncModeBar()
        {
            _syncingModeBar = true;
            try
            {
                ModeBar.SelectedItem = Vm.IsWorkingCopyMode ? ModeFileStatus
                                     : Vm.IsReflogMode ? ModeReflog
                                     : ModeLogHistory;
            }
            finally { _syncingModeBar = false; }
        }

        private async void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (_syncingModeBar) return;
            if (sender.SelectedItem == ModeFileStatus)
            {
                await Vm.EnterWorkingCopyAsync();
            }
            else if (sender.SelectedItem == ModeLogHistory)
            {
                Vm.ExitWorkingCopy();
                Vm.ExitReflog();
            }
            else if (sender.SelectedItem == ModeReflog)
            {
                await Vm.EnterReflogAsync();
            }
            else
            {
                // Search is an action on the current list, not a mode of its own: put the caret in
                // the box and snap the tab back to whatever is actually showing.
                SyncModeBar();
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
            }
        }

        // ---- Shared guards ---------------------------------------------------------------------

        /// <summary>False (after explaining why) when there is no repository, or when one operation
        /// is already half-finished — git refuses to start a second one, so offering it would only
        /// produce that refusal.</summary>
        private async Task<bool> RequireIdleRepositoryAsync(string title)
        {
            if (!Vm.HasRepository)
            {
                await ShowMessageAsync(title, "Open a repository first.");
                return false;
            }
            if (Vm.HasActiveOperation)
            {
                await ShowMessageAsync(title,
                    $"{Vm.StateTitle}. Finish or abort it first — the banner at the top of the "
                    + "window has the actions.");
                return false;
            }
            return true;
        }

        /// <summary>The pull flow's dirty-tree warning, reused: same risk, same two remedies, with
        /// the verb swapped so it reads as what is actually about to happen. Returns false when the
        /// user backs out.</summary>
        private async Task<bool> ConfirmDirtyTreeAsync(string title, string verb)
        {
            var (tracked, untracked) = await Vm.CountWorkingTreeAsync();
            if (tracked == 0 && untracked == 0)
                return true;

            return await ConfirmAsync(
                title,
                WorkingTreeWarning.Describe(tracked, untracked, Vm.CurrentBranchName, verb),
                "Continue Anyway");
        }

    }
}
