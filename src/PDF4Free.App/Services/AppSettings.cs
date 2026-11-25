using System.Text.Json;

namespace PDF4Free.App.Services;

/// <summary>User preferences and small bits of state kept between runs (%AppData%\PDF4Free\settings.json).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<string> RecentFiles { get; set; } = [];
    public string Author { get; set; } = Environment.UserName;
    public string OcrLanguage { get; set; } = "";
    public bool HighlightFormFields { get; set; } = true;
    public double NavigationWidth { get; set; } = 250;

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDF4Free");

    public static string SignatureFolder => Path.Combine(Folder, "signatures");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to store them must not interrupt work.
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }

    /// <summary>Saved signature images (PNG files), newest first.</summary>
    public static List<string> SavedSignatures()
    {
        if (!Directory.Exists(SignatureFolder)) return [];
        return [.. Directory.GetFiles(SignatureFolder, "*.png").OrderByDescending(File.GetLastWriteTimeUtc)];
    }

    public static string SaveSignature(byte[] png)
    {
        Directory.CreateDirectory(SignatureFolder);
        var path = Path.Combine(SignatureFolder, $"signature-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(path, png);
        return path;
    }
}
