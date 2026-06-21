using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Persists the recently-opened repositories as JSON in the app's local data folder, so the
    /// home screen can offer them again after a restart (CORE-002).
    /// </summary>
    public static class RecentRepositoriesStore
    {
        private const int MaxEntries = 10;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string FilePath
        {
            get
            {
                // Packaged app: a writable per-user folder that survives restarts.
                string dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                return Path.Combine(dir, "recent.json");
            }
        }

        public static List<RecentRepository> Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                    return new List<RecentRepository>();
                string json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<RecentRepository>>(json);
                return list ?? new List<RecentRepository>();
            }
            catch
            {
                return new List<RecentRepository>();
            }
        }

        /// <summary>Moves <paramref name="repo"/> to the top of the list (dedup by path) and saves.</summary>
        public static List<RecentRepository> Add(RepositoryInfo repo)
        {
            var list = Load();
            list.RemoveAll(r => string.Equals(r.Path, repo.RootPath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new RecentRepository
            {
                Name = repo.Name,
                Path = repo.RootPath,
                LastOpenedUtc = DateTimeOffset.UtcNow,
            });
            if (list.Count > MaxEntries)
                list = list.Take(MaxEntries).ToList();
            Save(list);
            return list;
        }

        private static void Save(List<RecentRepository> list)
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOptions));
            }
            catch
            {
                // Persisting recents is best-effort; ignore IO failures.
            }
        }
    }
}
