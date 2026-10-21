using System.IO;
using System.Text.Json;

namespace AirPDF.Services;

public sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = [];
    public string ExportMode { get; set; } = nameof(WordExportMode.EditableText);
    public string OcrLanguage { get; set; } = "auto";
    public string LastFitMode { get; set; } = "Page";
    public double LastZoom { get; set; } = 1.15;
    public string Theme { get; set; } = nameof(AppTheme.Light);
    public bool SidebarOpen { get; set; } = false;
    public double SidebarWidth { get; set; } = 230;
    public string SidebarTab { get; set; } = "Pages";
}

public static class AppSettingsStore
{
    private const int MaxRecentFiles = 8;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static AppSettings? _cache;

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AirPDF");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        lock (Gate)
        {
            if (_cache is not null)
                return Clone(_cache);

            try
            {
                if (!File.Exists(SettingsPath))
                {
                    _cache = new AppSettings();
                    return Clone(_cache);
                }

                var json = File.ReadAllText(SettingsPath);
                _cache = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                _cache = new AppSettings();
            }

            return Clone(_cache);
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            _cache = Clone(settings);
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                var json = JsonSerializer.Serialize(_cache, JsonOptions);
                var temp = SettingsPath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, SettingsPath, overwrite: true);
            }
            catch
            {
                // Settings must never crash the app.
            }
        }
    }

    public static void Update(Action<AppSettings> mutator)
    {
        var settings = Load();
        mutator(settings);
        Save(settings);
    }

    public static IReadOnlyList<string> GetRecentFiles(bool pruneMissing = true)
    {
        var settings = Load();
        var recent = settings.RecentFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecentFiles)
            .ToList();

        if (pruneMissing)
        {
            var existing = recent.Where(File.Exists).ToList();
            if (existing.Count != recent.Count || existing.Count != settings.RecentFiles.Count)
            {
                settings.RecentFiles = existing;
                Save(settings);
            }

            return existing;
        }

        return recent;
    }

    public static void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try { path = Path.GetFullPath(path); }
        catch { return; }

        if (!File.Exists(path))
            return;

        Update(settings =>
        {
            settings.RecentFiles.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            settings.RecentFiles.Insert(0, path);
            if (settings.RecentFiles.Count > MaxRecentFiles)
                settings.RecentFiles = settings.RecentFiles.Take(MaxRecentFiles).ToList();
        });
    }

    public static void RemoveRecentFile(string path) =>
        Update(settings =>
        {
            settings.RecentFiles.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        });

    public static void ClearRecentFiles() =>
        Update(settings => settings.RecentFiles.Clear());

    private static AppSettings Clone(AppSettings source) => new()
    {
        RecentFiles = [.. source.RecentFiles],
        ExportMode = source.ExportMode,
        OcrLanguage = source.OcrLanguage,
        LastFitMode = source.LastFitMode,
        LastZoom = source.LastZoom,
        Theme = source.Theme,
        SidebarOpen = source.SidebarOpen,
        SidebarWidth = source.SidebarWidth,
        SidebarTab = source.SidebarTab
    };
}
