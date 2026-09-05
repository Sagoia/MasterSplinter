using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>User preferences (STATUS-006). One JSON object in the app's local data folder.</summary>
    public sealed class AppSettings
    {
        /// <summary>External editor command; "{path}" is replaced with the file's absolute path.
        /// Blank means "open with the shell association".</summary>
        public string EditorCommand { get; set; } = "";

        /// <summary>MERGE-004: the merge tool passed to `git mergetool --tool=`. A plain name git
        /// knows ("vscode", "kdiff3", "p4merge", …) or one configured as mergetool.&lt;name&gt;.cmd.
        /// Blank defers to the repository's own merge.tool setting, which is the right default —
        /// a user who has configured git already does not want this box second-guessing it.</summary>
        public string MergeTool { get; set; } = "";
    }

    /// <summary>
    /// Shared JSON-file plumbing for the two small preference stores.
    /// <para>
    /// Both are <b>best effort</b>: a missing, unreadable or corrupt file yields the default rather
    /// than throwing. These hold conveniences (the recent list, an editor command), so failing the
    /// operation the user actually asked for because a preference file is unreadable would be worse
    /// than quietly starting from defaults.
    /// </para>
    /// <para>
    /// The directory is injected rather than read from <c>ApplicationData.Current</c>, which is what
    /// moves these out of the WinUI assembly and makes them testable against a temp folder.
    /// </para>
    /// </summary>
    public abstract class JsonFileStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        protected JsonFileStore(string directory, string fileName)
            => FilePath = Path.Combine(directory, fileName);

        protected string FilePath { get; }

        protected T LoadOrDefault<T>() where T : new()
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath)) ?? new T()
                    : new T();
            }
            catch
            {
                return new T();
            }
        }

        protected void SaveBestEffort<T>(T value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(value, JsonOptions));
            }
            catch
            {
                // Persisting a preference is best effort; an IO failure must not surface as an error.
            }
        }
    }

    /// <summary>Persists <see cref="AppSettings"/> as JSON.</summary>
    public sealed class SettingsStore : JsonFileStore
    {
        public SettingsStore(string directory) : base(directory, "settings.json") { }

        public AppSettings Load() => LoadOrDefault<AppSettings>();

        public void Save(AppSettings settings) => SaveBestEffort(settings);
    }

    /// <summary>
    /// The recently-opened repositories, newest first, so the home screen can offer them again after
    /// a restart (CORE-002).
    /// </summary>
    public sealed class RecentRepositoriesStore : JsonFileStore
    {
        public const int MaxEntries = 10;

        public RecentRepositoriesStore(string directory) : base(directory, "recent.json") { }

        public List<RecentRepository> Load() => LoadOrDefault<List<RecentRepository>>();

        /// <summary>
        /// Moves <paramref name="repo"/> to the top (dedup by path) and saves, returning the new list.
        /// <para>
        /// Paths compare case-insensitively: Windows would otherwise let the same repository appear
        /// twice under different casing, which is exactly the duplicate a recent list must not show.
        /// </para>
        /// </summary>
        public List<RecentRepository> Add(RepositoryInfo repo)
        {
            List<RecentRepository> list = Load();
            list.RemoveAll(r => string.Equals(r.Path, repo.RootPath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new RecentRepository
            {
                Name = repo.Name,
                Path = repo.RootPath,
                LastOpenedUtc = DateTimeOffset.UtcNow,
            });
            if (list.Count > MaxEntries)
                list = list.Take(MaxEntries).ToList();
            SaveBestEffort(list);
            return list;
        }
    }

    /// <summary>
    /// Where the app's preference stores live. Pure wiring: the host calls <see cref="UseDirectory"/>
    /// once at startup with its platform's local-data path (on Windows,
    /// <c>ApplicationData.Current.LocalFolder.Path</c> — a WinUI type Core must not reference).
    /// <para>
    /// Tests construct the stores directly against a temp folder and never touch this.
    /// </para>
    /// </summary>
    public static class AppStores
    {
        private static SettingsStore? _settings;
        private static RecentRepositoriesStore? _recent;

        public static void UseDirectory(string directory)
        {
            _settings = new SettingsStore(directory);
            _recent = new RecentRepositoriesStore(directory);
        }

        public static SettingsStore Settings =>
            _settings ?? throw new InvalidOperationException(
                "AppStores.UseDirectory must be called during startup before the stores are used.");

        public static RecentRepositoriesStore Recent =>
            _recent ?? throw new InvalidOperationException(
                "AppStores.UseDirectory must be called during startup before the stores are used.");
    }
}
