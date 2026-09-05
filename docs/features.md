# Features by phase

Task IDs (`DIFF-003`, `REMOTE-009`, …) appear verbatim in code comments — grep for one to find its
implementation. This table maps each phase to where it lives.

| Phase | Feature | IDs | Native | C# |
|---|---|---|---|---|
| **1** | Read-only viewer: open repo, history, refs, changed files, unified diff, recent list, search box, theme toggle | `CORE-001..003` | `MsGitIsRepository` `MsGitOpenRepository` `MsGitLog` `MsGitRefDetails` `MsGitCommitFiles` `MsGitFileDiff` `MsGitFileAtCommit` | `GitRepository.Log/ListRefs/ChangedFiles/FileDiff`, `MainViewModel` loading + sidebar |
| **2** | Diff viewer: shortstat, side-by-side, syntax highlighting, whitespace modes, binary/image preview, compare two commits or refs | `DIFF-001..007` | `MsGitCommitShortStat` `MsGitRange*` `MsGitFileBytesAtCommit` | `ParseUnifiedDiff`, `SideBySideBuilder`, `SyntaxHighlight`, compare state |
| **3** | Working tree: grouped status, worktree diffs, manual + watched refresh, open-in-editor, reveal in Explorer | `STATUS-001..007` | `MsGitStatus` `MsGitWorkTreeFileDiff` | `GitRepository.Status/WorkTreeDiff`, `StatusGroups`, `RepositoryWatcher`, `SettingsStore`, `EditorLauncher` |
| **4** | Staging & commit — the first **writes**: stage/unstage/stage-all/discard/commit/amend | `COMMIT-001,004..007` | `MsGitStagePaths` `MsGitStageAll` `MsGitUnstagePaths` `MsGitDiscardPaths` `MsGitCommit` `MsGitHeadMessage` | `RunStatusMutationAsync`, commit editor, watcher-suppression window |
| **5** | Branches & tags: create/rename/delete, checkout, annotated + lightweight tags, ahead/behind | `BR-001..007` `TAG-001..003` | `MsGitCheckout` `MsGitCreateBranch` `MsGitDeleteBranch` `MsGitRenameBranch` `MsGitCreateTag` `MsGitDeleteTag` `MsGitAheadBehind` | `RunRefMutationAsync`, sidebar refs, `NormalizeMessage` |
| **6** | Remotes: list/edit URLs, fetch, pull (`--ff-only`), push (`-u`), streaming progress + cancel | `REMOTE-001..009` | `MsGitRemotes` `MsGitSetRemoteUrl` `MsGitFetch` `MsGitPull` `MsGitPush` | `RunRemoteMutationAsync`, `GitProgressDialog`, `GitErrorHints` |
| **7** | Merge, rebase, cherry-pick, revert + continue/skip/abort, repository-state banner, external mergetool | `MERGE-001..004` `REBASE-001,002` `CHERRY-001,002` `REVERT-001` | `MsGitMerge` `MsGitRebase` `MsGitCherryPick` `MsGitRevert` `MsGitSequencerAction` `MsGitMergeTool` `MsGitRepositoryState` | `RunSequencerMutationAsync`, state banner, combined-diff parsing |
| **8** | Stash, blame, git-backed commit search, reflog | `STASH-001..004` `BLAME-001` `SEARCH-001,002` `REFLOG-001` | `MsGitStash*` `MsGitBlame` `MsGitSearchLog` `MsGitReflog` | `BlameWindow`, search modes, reflog mode |

## Deferred

Carried forward deliberately, with the reasoning recorded where it matters:

| Item | Note |
|---|---|
| `BISECT-001` | The Phase 7 repository-state banner is its natural host. |
| `COMMIT-008` hunk staging | `apply --cached [-R] -`; the stdin plumbing is already in place. |
| `COMMIT-009/010` templates & signing | `SettingsStore` knobs + extra `Commit` argv. |
| Clone; add/remove remotes; submodules | Phase 6 does URL editing only. |
| Merge/rebase on a diverged pull; force push | Pull is `--ff-only` by choice; nothing in the ABI forces. |
| Interactive rebase, `--squash`, resolve-using-ours/theirs | Excluded by the "never force, never interactive" rule, pinned by guard tests. |
| Reflog undo | Reflog is read-only today. |

## Design rules that hold across phases

- **Nothing forces.** Git's own refusal is the message the user sees — no `--force`, no `-X ours/theirs`, no
  `rebase -i`, no `--autostash`. Guard tests assert this.
- **Ref names are never validated locally.** `git check-ref-format` produces a better message than we could,
  and it reaches the UI verbatim through the ERR channel.
- **A conflict is a normal outcome**, not an error — it arrives as ERR, the repo is left mid-operation on
  purpose, and the view refreshes anyway.
- **Every git message sourced from a `TextBox`** goes through `NormalizeMessage` (the bare-`\r` trap).
- **Untracked files are their own category.** They never block a branch *switch*, but they do block a
  fast-forward when an incoming commit adds the same path — so the pull warning counts them separately from
  tracked changes.
