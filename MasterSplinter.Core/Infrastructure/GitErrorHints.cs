using System;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>
    /// REMOTE-009: turns a failed git command's raw output into something a user can act on.
    ///
    /// Git's failure text is accurate but assumes a terminal ("could not read Username for
    /// 'https://…': terminal prompts disabled" is literally true, and literally unhelpful in a
    /// GUI). Each hint below is one leading sentence naming the fix; the raw output is always kept
    /// underneath, because it is the only thing that can be pasted into a bug report.
    ///
    /// Phase 7 widened this past the remote commands: merge/rebase/cherry-pick/revert stopping on
    /// a conflict lands here too, and it is not a failure — see <see cref="IsConflict"/>.
    /// </summary>
    public static class GitErrorHints
    {
        /// <summary>The hint for <paramref name="gitOutput"/>, or null when nothing recognizable
        /// is in it (in which case git's own text stands on its own).</summary>
        public static string? HintFor(string? gitOutput)
        {
            if (string.IsNullOrWhiteSpace(gitOutput))
                return null;

            // Phase 7, and first: a stopped merge/rebase/cherry-pick/revert is the one "failure"
            // that is a normal outcome, and the advice for it ("go and resolve") is unlike every
            // remote hint below.
            if (IsConflict(gitOutput))
            {
                return "Git stopped because the changes overlap. Resolve the conflicted files in "
                     + "the working copy — the banner at the top of the window carries the "
                     + "continue and abort actions.";
            }

            // Phase 8 stash hints, above the remote ones: they share vocabulary ("would be
            // overwritten") with the checkout refusals further down.
            if (Has(gitOutput, "No stash entries found") || Has(gitOutput, "No stash found"))
            {
                return "There is nothing in the stash. It may have been dropped, or popped from "
                     + "another window since this list was read.";
            }

            if (Has(gitOutput, "could not restore untracked files"))
            {
                return "Some untracked files in the stash already exist in the working tree, so "
                     + "git stopped rather than overwrite them. Move or delete them and apply again "
                     + "— the stash entry is untouched.";
            }

            if (Has(gitOutput, "is not a stash-like commit") || Has(gitOutput, "is not a valid reference"))
            {
                return "That stash entry no longer exists. Stash numbers shift when an entry is "
                     + "dropped or popped — refresh and try again.";
            }

            // Ordered most-specific first: an SSH key failure also mentions "denied", and a
            // non-fast-forward rejection also mentions "failed to push".
            if (Has(gitOutput, "terminal prompts disabled") ||
                Has(gitOutput, "could not read Username") ||
                Has(gitOutput, "could not read Password"))
            {
                return "Git needs credentials for this remote but has no way to ask for them. "
                     + "Set up a credential helper (Git Credential Manager) or switch the remote "
                     + "to SSH.";
            }

            if (Has(gitOutput, "Permission denied (publickey"))
            {
                return "The remote rejected your SSH key. Check that the key is loaded in your "
                     + "agent (ssh-add -l) and that its public half is registered with the host.";
            }

            if (Has(gitOutput, "Host key verification failed"))
            {
                return "This host is not in your known_hosts file, and it cannot be confirmed from "
                     + "here. Connect once from a terminal (ssh -T <host>) to verify and record "
                     + "the host key.";
            }

            if (Has(gitOutput, "Authentication failed") || Has(gitOutput, "Invalid username or password"))
            {
                return "Authentication was rejected. The stored token or password is likely wrong "
                     + "or expired — update it in your credential helper and try again.";
            }

            if (Has(gitOutput, "Repository not found") || Has(gitOutput, "does not appear to be a git repository"))
            {
                return "The remote URL does not point at a repository you can reach. Check the URL "
                     + "(and, on a private repository, that your account has access).";
            }

            if (Has(gitOutput, "non-fast-forward") || Has(gitOutput, "fetch first") ||
                Has(gitOutput, "Updates were rejected"))
            {
                return "The remote has commits you do not have yet. Fetch and pull first, then "
                     + "push again.";
            }

            // MUST come before the tracked-file case below: git's untracked message contains the
            // same "would be overwritten by merge" phrase, so the generic match would swallow it —
            // and then tell the user to commit or discard changes to a file git never tracked,
            // contradicting git's own "move or remove them" line right underneath.
            if (Has(gitOutput, "untracked working tree files would be overwritten"))
            {
                return "Files you have never committed are sitting where the incoming commits want "
                     + "to create files of the same name. Move, rename or delete the ones listed "
                     + "below, then pull again.";
            }

            if (Has(gitOutput, "would be overwritten by merge") ||
                Has(gitOutput, "would be overwritten by checkout"))
            {
                return "The incoming commits change files you have uncommitted edits in. Commit or "
                     + "discard those edits, then pull again.";
            }

            if (Has(gitOutput, "Not possible to fast-forward") || Has(gitOutput, "divergent branches"))
            {
                return "Your branch and its upstream have diverged, so there is no fast-forward "
                     + "to make. Fetch, then merge or rebase the upstream branch into yours "
                     + "(Actions ▸ Merge… / Rebase…).";
            }

            if (Has(gitOutput, "no upstream") || Has(gitOutput, "no tracking information"))
            {
                return "This branch has no upstream yet. Push it with “Set upstream” "
                     + "checked to publish it and start tracking.";
            }

            if (Has(gitOutput, "Could not resolve host") || Has(gitOutput, "unable to access") ||
                Has(gitOutput, "Connection timed out") || Has(gitOutput, "Could not read from remote repository"))
            {
                return "The remote could not be reached. Check your network connection, any proxy "
                     + "settings, and that the remote URL is correct.";
            }

            return null;
        }

        /// <summary>
        /// True when <paramref name="gitOutput"/> is git reporting that a merge, rebase,
        /// cherry-pick or revert STOPPED on a conflict rather than failing outright. The exit code
        /// is non-zero either way, so the text is the only thing that separates "you have work to
        /// do" from "nothing happened" — and the two deserve very different wording.
        /// </summary>
        public static bool IsConflict(string? gitOutput)
        {
            if (string.IsNullOrWhiteSpace(gitOutput))
                return false;
            // "CONFLICT (content):"       — every conflicting merge/cherry-pick/revert/rebase
            // "Automatic merge failed"    — merge's own summary line
            // "could not apply <sha>"     — cherry-pick / rebase stopping on a commit
            // "after resolving the conflicts" — the advice block git prints underneath
            return Has(gitOutput, "CONFLICT (")
                || Has(gitOutput, "Automatic merge failed")
                || Has(gitOutput, "could not apply")
                || Has(gitOutput, "after resolving the conflicts")
                || Has(gitOutput, "fix conflicts and then commit");
        }

        /// <summary>Git's output with the hint (when there is one) prefixed as its own paragraph.</summary>
        public static string Decorate(string? gitOutput)
        {
            string raw = (gitOutput ?? "").Trim();
            string? hint = HintFor(raw);
            if (hint == null)
                return raw;
            return raw.Length == 0 ? hint : hint + "\n\n" + raw;
        }

        private static bool Has(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
