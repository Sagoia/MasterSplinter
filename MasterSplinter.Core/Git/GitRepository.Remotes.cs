using System;
using System.Collections.Generic;
using System.Linq;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Remote listing, URL editing and the three network commands.
    // Mirrors GitBackend.Remotes.cpp.
    public sealed partial class GitRepository
    {
        // ---- Remotes (Phase 6, REMOTE-001..009) ------------------------------------------------

        /// <summary>REMOTE-001. Parses `git remote -v`, whose two lines per remote
        /// ("&lt;name&gt;\t&lt;url&gt; (fetch)" then "(push)") are folded into one record.</summary>
        public IReadOnlyList<RemoteInfo> ListRemotes()
        {
            // Insertion-ordered so the dialog lists remotes the way git does (origin first).
            var fetchUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            var pushUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (string line in NativeLogic.GitRemotes(RootPath).Split('\n'))
            {
                string l = line.Trim('\r');
                if (l.Length == 0)
                    continue;

                int tab = l.IndexOf('\t');
                if (tab <= 0)
                    continue;
                string name = l[..tab];
                string rest = l[(tab + 1)..];

                // The trailing "(fetch)"/"(push)" marker says which URL this line carries. A URL
                // can itself contain spaces, so split from the END, not the start.
                int space = rest.LastIndexOf(' ');
                string kind = space >= 0 ? rest[(space + 1)..] : "";
                string url = space >= 0 ? rest[..space] : rest;

                if (!fetchUrls.ContainsKey(name) && !pushUrls.ContainsKey(name))
                    order.Add(name);
                if (kind == "(push)")
                    pushUrls[name] = url;
                else
                    fetchUrls[name] = url;
            }

            return order
                .Select(n => new RemoteInfo(n,
                    fetchUrls.TryGetValue(n, out string? f) ? f : "",
                    pushUrls.TryGetValue(n, out string? p) ? p : ""))
                .ToList();
        }

        /// <summary>REMOTE-008. Callers validate the URL before offering to save.</summary>
        public string? SetRemoteUrl(string name, string url, bool pushUrl)
            => ParseOkErr(NativeLogic.GitSetRemoteUrl(RootPath, name, url.Trim(), pushUrl));

        /// <summary>REMOTE-002. <paramref name="onProgress"/> receives git's output as it arrives
        /// (and an empty string as a heartbeat); returning false cancels the command.</summary>
        public string? Fetch(string remote, bool allRemotes, bool prune, bool tags,
                             Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitFetch(RootPath, remote, allRemotes, prune, tags, onProgress));

        /// <summary>REMOTE-004, fast-forward only. Empty remote/branch uses the branch's own
        /// upstream.</summary>
        public string? Pull(string remote, string branch, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitPull(RootPath, remote, branch, onProgress));

        /// <summary>REMOTE-005; <paramref name="setUpstream"/> is what publishes a new branch with
        /// tracking (REMOTE-006).</summary>
        public string? Push(string remote, string branch, bool setUpstream, bool pushTags,
                            Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitPush(RootPath, remote, branch, setUpstream, pushTags, onProgress));
    }
}
