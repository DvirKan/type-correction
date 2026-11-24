using System.Text.Json;
using Microsoft.Win32;

namespace TypeCorrection;

/// <summary>User settings. Stored in %AppData%\TypeCorrection\settings.json. Never contains typed text.</summary>
internal sealed class Settings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Process names (without .exe) where the app does nothing at all.</summary>
    public List<string> ExcludedApps { get; set; } =
    [
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "Dashlane", "LastPass",
        "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "mstsc",
    ];

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TypeCorrection");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public bool IsExcluded(string processName) =>
        ExcludedApps.Any(a => string.Equals(Path.GetFileNameWithoutExtension(a.Trim()), processName,
            StringComparison.OrdinalIgnoreCase));

    // ---- Start with Windows (HKCU\...\Run, no admin needed) ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TypeCorrection";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --startup");
            else key.DeleteValue(RunValue, false);
        }
    }
}
