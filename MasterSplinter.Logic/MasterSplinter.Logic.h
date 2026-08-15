// The following ifdef block is the standard way of creating macros which make exporting
// from a DLL simpler. All files within this DLL are compiled with the MASTERSPLINTERLOGIC_EXPORTS
// symbol defined on the command line. This symbol should not be defined on any project
// that uses this DLL. This way any other project whose source files include this file see
// MASTERSPLINTERLOGIC_API functions as being imported from a DLL, whereas this DLL sees symbols
// defined with this macro as being exported.
#ifdef MASTERSPLINTERLOGIC_EXPORTS
#define MASTERSPLINTERLOGIC_API __declspec(dllexport)
#else
#define MASTERSPLINTERLOGIC_API __declspec(dllimport)
#endif

// ---------------------------------------------------------------------------------------------
// Flat C ABI — this is the interop boundary consumed by the C# app via P/Invoke, and the same
// boundary any other OS's FFI can call. Keep it `extern "C"` (no name mangling, C-compatible
// types only) and keep the real, portable logic behind it (no <windows.h> in the core).
// ---------------------------------------------------------------------------------------------
extern "C" {
	// ---- Lifecycle (do DLL/state setup HERE, not in DllMain) --------------------------------
	// Call MsLogicInitialize() once after the library loads, before any other call; call
	// MsLogicShutdown() once at exit. These are portable (identical on every OS) and run outside
	// the loader lock, so real one-time work (opening libgit2, warming caches, etc.) is safe here.
	MASTERSPLINTERLOGIC_API bool MsLogicInitialize(void);
	MASTERSPLINTERLOGIC_API void MsLogicShutdown(void);

	// Returns a static, null-terminated UTF-8 version string. The caller must NOT free it.
	MASTERSPLINTERLOGIC_API const char* MsLogicVersion(void);

	// Trivial sample so the round-trip is verifiable from C#.
	MASTERSPLINTERLOGIC_API int MsLogicAdd(int a, int b);

	// ---- Read-only git backend (implemented in GitBackend.cpp) ------------------------------
	// All strings are UTF-8. Every char* return is heap-allocated by this DLL and MUST be
	// released by the caller via MsGitFree() (never freed across the FFI boundary in C#).
	// These shell out to git.exe (resolved via PATH), mirroring how TortoiseGit reads data.
	//
	// Field separator inside a record is 0x1F; records are separated by 0x1E.

	// True if 'path' is inside a git work tree (git rev-parse --is-inside-work-tree).
	MASTERSPLINTERLOGIC_API bool MsGitIsRepository(const char* path);

	// "OK\x1f<toplevel>\x1f<branch>" on success, or "ERR\x1f<message>" if not a repository.
	MASTERSPLINTERLOGIC_API char* MsGitOpenRepository(const char* path);

	// One record per commit (separated by 0x1E); fields (separated by 0x1F) are:
	// fullHash, shortHash, parents, authorName, authorEmail, authorDateISO,
	// committerName, committerEmail, committerDateISO, refDecorations, subject, body.
	// order: 0=date, 1=topo, 2=reverse-date, 3=author-date. maxCount<=0 means no limit.
	MASTERSPLINTERLOGIC_API char* MsGitLog(const char* root, int order, int maxCount);

	// One record per ref (records separated by 0x1E, fields by 0x1F) covering refs/heads,
	// refs/tags and refs/remotes in refname order. Always exactly 8 fields, several of which are
	// routinely empty — the field COUNT is what makes positional parsing safe:
	//   0 refname      full ref name ("refs/heads/main")
	//   1 objectname   SHA the ref points at (the TAG OBJECT for an annotated tag)
	//   2 peeled       commit an annotated tag points at; empty for everything else
	//   3 objecttype   "commit" | "tag"  ("tag" => annotated tag)
	//   4 upstream     short upstream name ("origin/main"); empty when there is none
	//   5 track        "ahead 2, behind 1" | "ahead 2" | "behind 1" | "gone" | "" (in sync)
	//   6 head         "*" for the checked-out branch, otherwise a single space
	//   7 symref       non-empty only for symbolic refs (e.g. refs/remotes/origin/HEAD)
	// Empty string on error. NOTE: for-each-ref uses "%xx" hex escapes, NOT log's "%xNN".
	MASTERSPLINTERLOGIC_API char* MsGitRefDetails(const char* root);

	// Tab-separated git name-status for one commit: "<status>\t<path>[\t<newPath>]" per line.
	MASTERSPLINTERLOGIC_API char* MsGitCommitFiles(const char* root, const char* sha);

	// One-line "--shortstat" summary for a commit ("N files changed, X insertions(+), Y deletions(-)").
	MASTERSPLINTERLOGIC_API char* MsGitCommitShortStat(const char* root, const char* sha);

	// Unified diff text for one file in one commit (no commit header).
	// wsMode: 0 = honor whitespace, 1 = --ignore-space-change, 2 = --ignore-all-space.
	MASTERSPLINTERLOGIC_API char* MsGitFileDiff(const char* root, const char* sha, const char* path, int wsMode);

	// Full file content as of that commit (git show <sha>:<path>); empty if absent.
	MASTERSPLINTERLOGIC_API char* MsGitFileAtCommit(const char* root, const char* sha, const char* path);

	// ---- Compare two commits / refs (a..b) — also used for branch/tag/HEAD comparison ----------
	// a and b may be full SHAs OR ref names (branch/tag/HEAD); git diff accepts either.

	// Tab-separated name-status for the diff between a and b ("<status>\t<path>[\t<newPath>]").
	MASTERSPLINTERLOGIC_API char* MsGitRangeFiles(const char* root, const char* a, const char* b);

	// One-line "--shortstat" summary for the diff between a and b.
	MASTERSPLINTERLOGIC_API char* MsGitRangeShortStat(const char* root, const char* a, const char* b);

	// Unified diff text for one file between a and b. wsMode as in MsGitFileDiff.
	MASTERSPLINTERLOGIC_API char* MsGitRangeFileDiff(const char* root, const char* a, const char* b,
	                                                 const char* path, int wsMode);

	// ---- Working tree status (Phase 3) ---------------------------------------------------------

	// git status --porcelain=v1 -z with every NUL separator translated to 0x1E (a NUL-separated
	// payload would be truncated at the first NUL by the managed marshaller). Records are
	// "XY <path>"; when X or Y is R/C the record is followed by one extra record holding the
	// ORIGINAL path (new path first — -z order is reversed vs the human-readable format).
	// Untracked files appear as "?? <path>" (--untracked-files=all). Empty string on error.
	MASTERSPLINTERLOGIC_API char* MsGitStatus(const char* root);

	// Unified diff for one working-tree file. area: 0 = unstaged (worktree vs index),
	// 1 = staged (index vs HEAD, --cached), 2 = untracked (--no-index vs /dev/null, i.e. the
	// whole file as additions). wsMode as in MsGitFileDiff.
	MASTERSPLINTERLOGIC_API char* MsGitWorkTreeFileDiff(const char* root, const char* path,
	                                                    int area, int wsMode);

	// Raw bytes of a file as of a commit/ref (git show <sha>:<path>), for binary/image previews.
	// Unlike the char*-as-string returns, the payload MAY contain NUL bytes; *outLen holds the
	// length and the caller must copy exactly that many bytes (not strlen). Returns nullptr on error.
	MASTERSPLINTERLOGIC_API char* MsGitFileBytesAtCommit(const char* root, const char* sha,
	                                                     const char* path, int* outLen);

	// ---- Staging & commit (Phase 4) — the first WRITE operations -------------------------------
	// All return "OK" on success or "ERR\x1f<message>" on failure (git's merged stdout+stderr,
	// trailing newlines trimmed). `paths` arguments hold one or more repo-relative paths
	// separated by 0x1E.

	// git add -A -- <paths...> : stages modifications, deletions, and untracked files alike.
	MASTERSPLINTERLOGIC_API char* MsGitStagePaths(const char* root, const char* paths);

	// git add -A : stages every change the status view shows.
	MASTERSPLINTERLOGIC_API char* MsGitStageAll(const char* root);

	// git restore --staged -- <paths...> (or git rm -r --cached -q on an unborn branch, i.e. a
	// fresh repo with no commits). For a staged rename pass BOTH the new and the old path.
	MASTERSPLINTERLOGIC_API char* MsGitUnstagePaths(const char* root, const char* paths);

	// git restore -- <paths...> : restores unstaged changes from the index. Tracked files only —
	// deleting an untracked file is the caller's job (it is a plain filesystem delete).
	MASTERSPLINTERLOGIC_API char* MsGitDiscardPaths(const char* root, const char* paths);

	// git commit [--amend] --cleanup=strip -F - with `message` (full UTF-8 commit message,
	// subject + blank line + body) fed via stdin. Empty/whitespace-only message returns ERR
	// without invoking git.
	MASTERSPLINTERLOGIC_API char* MsGitCommit(const char* root, const char* message, bool amend);

	// "OK\x1f<subject>\x1f<body>" for the HEAD commit (amend pre-fill), or "ERR\x1f<message>"
	// (e.g. no commits yet).
	MASTERSPLINTERLOGIC_API char* MsGitHeadMessage(const char* root);

	// ---- Branches & tags (Phase 5) -------------------------------------------------------------
	// Same "OK" / "ERR\x1f<message>" contract as the Phase 4 writes. Ref names are NOT validated
	// here: git's own check-ref-format produces a better message than we could, and it reaches
	// the UI verbatim through the ERR channel.

	// git switch <refName>, or git switch --detach <refName> for a commit. Deliberately NOT
	// forced — git carries uncommitted changes across when it safely can and refuses otherwise,
	// and that refusal is the message the caller shows.
	MASTERSPLINTERLOGIC_API char* MsGitCheckout(const char* root, const char* refName, bool detach);

	// checkout ? "git switch -c <name> [<startPoint>]" : "git branch <name> [<startPoint>]".
	// Empty startPoint means HEAD. Never forced, so an existing name fails loudly.
	MASTERSPLINTERLOGIC_API char* MsGitCreateBranch(const char* root, const char* name,
	                                                const char* startPoint, bool checkout);

	// git branch -d (safe) / -D (force). Callers must try force=false first and only offer
	// force=true after showing git's refusal.
	MASTERSPLINTERLOGIC_API char* MsGitDeleteBranch(const char* root, const char* name, bool force);

	// git branch -m <oldName> <newName> — works on the current branch, never clobbers.
	MASTERSPLINTERLOGIC_API char* MsGitRenameBranch(const char* root, const char* oldName,
	                                                const char* newName);

	// Blank message => lightweight "git tag <name> [<commitish>]"; otherwise annotated
	// "git tag -a -F - <name> [<commitish>]" with the message fed via stdin. Empty commitish
	// means HEAD.
	MASTERSPLINTERLOGIC_API char* MsGitCreateTag(const char* root, const char* name,
	                                             const char* commitish, const char* message);

	// git tag -d <name> (local only).
	MASTERSPLINTERLOGIC_API char* MsGitDeleteTag(const char* root, const char* name);

	// "<onlyInA>\t<onlyInB>" from git rev-list --left-right --count a...b; empty on error.
	// This is a READ op: it decorates the compare banner rather than acting.
	MASTERSPLINTERLOGIC_API char* MsGitAheadBehind(const char* root, const char* a, const char* b);

	// ---- Remotes (Phase 6) ---------------------------------------------------------------------
	// Same "OK" / "ERR\x1f<message>" contract as the Phase 4/5 writes. The three network commands
	// take an optional progress callback; all of them pass --progress to git and run with
	// GIT_TERMINAL_PROMPT=0, so a missing credential helper fails fast with a readable message
	// instead of leaving a child process blocked on a prompt a GUI can never answer.

	// Progress callback for the network commands. Called with each chunk of git's merged
	// stdout/stderr as it arrives (`bytes`/`length`, NOT NUL-terminated — use the length), and
	// with (NULL, 0) as a periodic heartbeat while the command runs. Return 0 to cancel: the child
	// is terminated and the command returns ERR with whatever it had produced. Return non-zero to
	// continue. Invoked on threads owned by this DLL, never after the originating call returns;
	// invocations are serialized, so the callback needs no lock of its own. May be NULL.
	typedef int (*MsGitProgressFn)(void* userData, const char* bytes, int length);

	// Raw `git remote -v` output ("<name>\t<url> (fetch)" / "(push)" per line); empty on error.
	MASTERSPLINTERLOGIC_API char* MsGitRemotes(const char* root);

	// git remote set-url [--push] <name> <url>. Editing only — Phase 6 does not add or remove
	// remotes.
	MASTERSPLINTERLOGIC_API char* MsGitSetRemoteUrl(const char* root, const char* name,
	                                                const char* url, bool pushUrl);

	// git fetch --progress [--all | <remote>] [--prune] [--tags].
	MASTERSPLINTERLOGIC_API char* MsGitFetch(const char* root, const char* remote, bool allRemotes,
	                                         bool prune, bool tags,
	                                         MsGitProgressFn cb, void* userData);

	// git pull --ff-only --progress [<remote> <branch>]. Empty remote/branch pulls from the
	// current branch's configured upstream. Deliberately fast-forward only: a diverged branch
	// gets git's refusal rather than an unrequested merge or rebase.
	MASTERSPLINTERLOGIC_API char* MsGitPull(const char* root, const char* remote, const char* branch,
	                                        MsGitProgressFn cb, void* userData);

	// git push --progress [--set-upstream] [--tags] <remote> <branch>. setUpstream is what
	// publishes a new branch with tracking. Never forced.
	MASTERSPLINTERLOGIC_API char* MsGitPush(const char* root, const char* remote, const char* branch,
	                                        bool setUpstream, bool pushTags,
	                                        MsGitProgressFn cb, void* userData);

	// ---- Merge / rebase / cherry-pick / revert (Phase 7) ----------------------------------------
	// Same "OK" / "ERR\x1f<message>" contract, and the same optional progress callback as the
	// network commands (these stream too, and a rebase over many commits can take a while).
	//
	// All of them run with GIT_EDITOR=true, GIT_SEQUENCE_EDITOR=true and GIT_TERMINAL_PROMPT=0:
	// these are the git commands that open an editor, and a GUI child blocked on an editor nobody
	// can see never returns. None of them force — no --squash, no -X ours/theirs, no rebase -i, no
	// --autosquash, no --autostash — so whatever git refuses reaches the user as git's own words.
	//
	// A conflict is reported as ERR (git exits non-zero), but it is a NORMAL outcome: the payload
	// is git's "CONFLICT (…)" text and the repository is left mid-operation on purpose. Callers
	// must refresh even on ERR, and use MsGitRepositoryState to find out where things stand.

	// git merge --no-edit [--no-ff] [--no-commit] -- <ref>
	MASTERSPLINTERLOGIC_API char* MsGitMerge(const char* root, const char* refName,
	                                         bool noFastForward, bool noCommit,
	                                         MsGitProgressFn cb, void* userData);

	// git rebase <upstream> — non-interactive only.
	MASTERSPLINTERLOGIC_API char* MsGitRebase(const char* root, const char* upstream,
	                                          MsGitProgressFn cb, void* userData);

	// git cherry-pick --no-edit [-n] <sha>... ; `shas` holds one or more commit ids separated by
	// 0x1E, applied in the order given (so the caller passes them OLDEST FIRST).
	MASTERSPLINTERLOGIC_API char* MsGitCherryPick(const char* root, const char* shas,
	                                              bool noCommit,
	                                              MsGitProgressFn cb, void* userData);

	// git revert --no-edit [-m <mainline>] [-n] <sha>. `mainline` is 1-based; 0 omits -m. Reverting
	// a merge commit REQUIRES it (git cannot guess which side to keep), and passing it for an
	// ordinary commit is an error — hence the explicit 0.
	MASTERSPLINTERLOGIC_API char* MsGitRevert(const char* root, const char* sha, int mainline,
	                                          bool noCommit, MsGitProgressFn cb, void* userData);

	// git <operation> --<action>: the continue/abort/skip half of all four operations. Both
	// arguments are validated against fixed allowlists and anything else returns ERR without
	// spawning git. Named strings rather than an int pair on purpose — a silently-swapped integer
	// contract has cost this ABI a shipped bug before (see MsGitWorkTreeFileDiff's `area`).
	//   operation "merge"                              -> action "continue" | "abort"
	//   operation "rebase" | "cherry-pick" | "revert"  -> action "continue" | "abort" | "skip"
	MASTERSPLINTERLOGIC_API char* MsGitSequencerAction(const char* root, const char* operation,
	                                                   const char* action,
	                                                   MsGitProgressFn cb, void* userData);

	// git mergetool --no-prompt [--tool=<tool>] -- <path>. Git extracts the BASE/LOCAL/REMOTE
	// temporaries, launches the tool, and stages the file itself when the tool exits cleanly. An
	// empty `tool` leaves the choice to the user's own merge.tool configuration. The call does not
	// return until the tool exits, so it always wants the progress callback (to stay cancellable).
	MASTERSPLINTERLOGIC_API char* MsGitMergeTool(const char* root, const char* path,
	                                             const char* tool,
	                                             MsGitProgressFn cb, void* userData);

	// Whether an operation is half-finished, and how far along:
	//   "OK\x1f<state>\x1f<detail>\x1f<step>\x1f<total>\x1f<message>"
	//   state   none | merging | rebasing | cherry-picking | reverting
	//   detail  the branch being rebased, or the short sha of MERGE_HEAD/CHERRY_PICK_HEAD/
	//           REVERT_HEAD; empty when state is none
	//   step    the rebase's current commit, total its last (both 0 when not rebasing)
	//   message MERGE_MSG with git's "# Conflicts:" comment lines removed — what the commit
	//           editor should open with; empty when state is none
	// "ERR\x1f<message>" only when the path is not a repository at all.
	MASTERSPLINTERLOGIC_API char* MsGitRepositoryState(const char* root);

	// ---- Stash, blame, search, reflog (Phase 8) ------------------------------------------------

	// One record per stash entry (records separated by 0x1E, fields by 0x1F), newest first:
	//   0 selector    "stash@{0}"  -- what apply/pop/drop take
	//   1 sha         the stash commit's full hash
	//   2 shortSha
	//   3 message     git's reflog subject: "WIP on main: 1a2b3c4 <subject>" or "On main: <text>"
	//   4 dateISO     author date of the stash commit
	//   5 author
	// Empty string when there are no stashes, and on error — an empty list either way.
	// NOTE: dropping or popping RENUMBERS every later entry, so a selector is only valid until the
	// next stash mutation. Callers must re-read this list after any of the three.
	MASTERSPLINTERLOGIC_API char* MsGitStashList(const char* root);

	// git stash push [--include-untracked] [--keep-index] [-m <message>]; "OK" / "ERR\x1f<message>".
	// A blank `message` omits -m and lets git compose its own "WIP on <branch>" text.
	// Returns ERR when git stashed NOTHING: `git stash push` exits 0 on a clean tree, and reporting
	// that as success would tell the user their work was parked when it is still sitting there.
	MASTERSPLINTERLOGIC_API char* MsGitStashSave(const char* root, const char* message,
	                                             bool includeUntracked, bool keepIndex);

	// git stash apply|pop|drop [<ref>]; "OK" / "ERR\x1f<message>". `ref` is a selector from
	// MsGitStashList, or empty for the most recent entry; anything not matching "stash@{<digits>}"
	// returns ERR without spawning git. A conflicting apply/pop exits non-zero with the markers
	// already written, so it arrives as ERR carrying git's own conflict text.
	MASTERSPLINTERLOGIC_API char* MsGitStashApply(const char* root, const char* ref);
	MASTERSPLINTERLOGIC_API char* MsGitStashPop(const char* root, const char* ref);
	MASTERSPLINTERLOGIC_API char* MsGitStashDrop(const char* root, const char* ref);

	// git blame --porcelain [-w] [<move flags>] <rev> -- <path>; empty `rev` means HEAD.
	// detectMoves selects how hard git looks for moved/copied lines — a NAME, not an int:
	//   "" | "none"  no detection      "file"    -M      (moved within this file)
	//   "commit"     -C                "any"     -C -C   (also from files the commit created)
	// Anything else returns ERR without spawning git.
	//
	// Returns "OK\x1f<raw --porcelain output>" or "ERR\x1f<message>". This read is OK/ERR-framed
	// (unlike MsGitLog and friends) because "that path is not in that revision" is a routine,
	// actionable failure whose message is worth keeping. Split on the FIRST 0x1F only: the payload
	// is file content and may contain 0x1F bytes of its own.
	//
	// A binary file is refused with ERR rather than returned: --porcelain content lines are raw
	// file bytes, and a NUL would truncate the whole payload at the managed marshaller.
	MASTERSPLINTERLOGIC_API char* MsGitBlame(const char* root, const char* rev, const char* path,
	                                         bool ignoreWhitespace, const char* detectMoves);

	// Commit search. Records are byte-identical to MsGitLog's 12-field layout, so one host-side
	// parser serves both. `mode` picks exactly ONE git predicate — deliberately one, because git
	// ANDs --grep with --author rather than ORing them, so a combined "message or author" search
	// would silently return the intersection:
	//   "message"  --grep=<query>       (--fixed-strings unless useRegex)
	//   "author"   --author=<query>
	//   "content"  -S<query>, or -G<query> when useRegex (search the diff text itself)
	//   "path"     <query> is used as the pathspec — "which commits touched this file"
	//   "hash"     resolve <query> to a commit and return just that one record
	// `pathFilter` narrows any mode further (appended after --). matchCase=false adds
	// --regexp-ignore-case; allBranches adds --all; `order` and `maxCount` are as in MsGitLog.
	//
	// Empty string — with no log walk — for: an unknown mode, a blank query AND blank pathFilter,
	// or a "hash" query that resolves to nothing (a typo must not surface as git's error text
	// rendered into the commit list).
	MASTERSPLINTERLOGIC_API char* MsGitSearchLog(const char* root, const char* mode,
	                                             const char* query, const char* pathFilter,
	                                             int order, int maxCount, bool matchCase,
	                                             bool useRegex, bool allBranches);

	// git reflog show [-n<maxCount>] <ref>; empty `ref` means HEAD. One record per entry (0x1E),
	// seven 0x1F-separated fields:
	//   0 selector   "HEAD@{3}"
	//   1 sha        2 shortSha
	//   3 reflogSubject  "commit: <subject>" / "pull: Fast-forward" / "checkout: moving from..."
	//   4 dateISO    5 author    6 commitSubject  (%s — often fuller than the reflog subject)
	// A ref with no reflog makes git exit non-zero, which yields "" — an empty list, not an error.
	MASTERSPLINTERLOGIC_API char* MsGitReflog(const char* root, const char* ref, int maxCount);

	// Frees any char* returned by the MsGit* functions above.
	MASTERSPLINTERLOGIC_API void MsGitFree(char* ptr);
}
