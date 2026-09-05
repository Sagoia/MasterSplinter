using System;
using System.IO;
using System.Linq;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Shape checks for a remote URL, run before offering to save one.
    /// <para>
    /// Deliberately shallow: this rejects the shapes git could never use, and leaves everything else
    /// to git — which is the only thing that actually knows whether a host resolves or a path is a
    /// repository. The point is a readable message at the dialog rather than a fetch that fails
    /// later for reasons the user cannot see.
    /// </para>
    /// </summary>
    public static class RemoteUrl
    {
        /// <summary>The problem with <paramref name="url"/>, or null when it looks usable.</summary>
        /// <param name="localPathExists">How to test a local path. Injected so the rules are
        /// testable without touching the filesystem; defaults to <see cref="Directory.Exists"/>.
        /// </param>
        public static string? Validate(string? url, Func<string, bool>? localPathExists = null)
        {
            localPathExists ??= Directory.Exists;

            string u = (url ?? "").Trim();
            if (u.Length == 0)
                return "Enter a URL.";
            if (u.Any(char.IsWhiteSpace))
                return "A remote URL cannot contain spaces.";

            // scheme://host/path — https, ssh, git, file, ftp(s)…
            int scheme = u.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                if (scheme == 0)
                    return "The URL is missing its scheme (for example https:// or ssh://).";
                if (u.Length <= scheme + 3)
                    return "The URL has a scheme but no host.";
                return null;
            }

            // scp-style "user@host:path" — what GitHub hands out for SSH.
            if (u.Contains(':') && u.Contains('@'))
                return null;

            // A local path (another clone on disk, or a bare repository) must actually exist.
            if (localPathExists(u))
                return null;

            return "Enter a URL like https://host/repo.git, git@host:owner/repo.git, "
                 + "or the path to a local repository.";
        }
    }
}
