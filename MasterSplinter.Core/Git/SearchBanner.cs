using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// The banner above a set of git-backed search results.
    /// <para>
    /// It names the predicate that actually ran, which matters because each mode is exactly ONE git
    /// predicate: git ANDs <c>--grep</c> with <c>--author</c> rather than ORing them, so a box
    /// claiming to search "message or author" would silently return the intersection. Saying
    /// "author X" rather than "search X" is what keeps an empty result readable.
    /// </para>
    /// </summary>
    public static class SearchBanner
    {
        public static string Describe(SearchMode mode, string query, string path, int count)
        {
            string what = mode switch
            {
                SearchMode.Author => $"author “{query}”",
                SearchMode.Content => $"changes containing “{query}”",
                SearchMode.Path => $"commits touching “{query}”",
                SearchMode.Hash => $"commit {query}",
                _ => $"message “{query}”",
            };

            // A blank query with a path filter is a path search by another route — describe what ran,
            // not what the mode dropdown happens to say.
            if (query.Length == 0)
                what = $"commits touching “{path}”";
            else if (path.Length > 0)
                what += $" under “{path}”";

            return $"{count} result{(count == 1 ? "" : "s")} for {what}";
        }
    }
}
