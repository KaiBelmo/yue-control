using System.Text.Json;

namespace Spotikey;

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\Spotikey\config.json.</summary>
public sealed class Config
{
    // Work through Windows media sessions - no extension needed.
    public string Next { get; set; } = "Ctrl+Alt+Right";
    public string Previous { get; set; } = "Ctrl+Alt+Left";
    public string PlayPause { get; set; } = "Ctrl+Alt+Down";
    public string NowPlaying { get; set; } = "Ctrl+Alt+Up";

    // Need the Chrome extension (they click buttons inside open.spotify.com). Empty = unbound.
    public string Shuffle { get; set; } = "Ctrl+Alt+S";
    public string Like { get; set; } = "Ctrl+Alt+L";
    public string VolumeUp { get; set; } = "Ctrl+Alt+PageUp";
    public string VolumeDown { get; set; } = "Ctrl+Alt+PageDown";
    public string Repeat { get; set; } = "";

    /// <summary>How long the now-playing popup stays visible.</summary>
    public double PopupSeconds { get; set; } = 3;

    /// <summary>Show the popup automatically after next / previous / play-pause.</summary>
    public bool ShowPopupOnAction { get; set; } = true;

    /// <summary>How many upcoming tracks to list in the popup (needs the extension).</summary>
    public int QueueRows { get; set; } = 5;

    /// <summary>Spotify volume change per hotkey press, 0.1 = 10%.</summary>
    public double VolumeStep { get; set; } = 0.1;

    /// <summary>Localhost port the Chrome extension connects to. Must match extension/background.js.</summary>
    public int BridgePort { get; set; } = 47321;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotikey");

    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Config Load()
    {
        Config cfg = new();
        try
        {
            if (File.Exists(FilePath))
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOptions) ?? new Config();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read {FilePath}, using default hotkeys.\n\n{ex.Message}",
                "Spotikey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Write back so newly added settings show up in the file.
        try { cfg.Save(); } catch { }
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
