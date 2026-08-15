using System.IO;
using System.Text.Json;

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

    /// <summary>Persists <see cref="AppSettings"/> as JSON, mirroring RecentRepositoriesStore.</summary>
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string FilePath
        {
            get
            {
                string dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                return Path.Combine(dir, "settings.json");
            }
        }

        public static AppSettings Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                    return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch
            {
                // Persisting settings is best-effort; ignore IO failures.
            }
        }
    }
}
