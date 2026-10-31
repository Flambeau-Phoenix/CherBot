using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CherBot.Desktop;

/// <summary>A remote engine (for example a Linux server on your network, or a CherBot Windows Service on this PC).</summary>
public sealed class RemoteProfile
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "http://127.0.0.1:8091";
    /// <summary>The API token, encrypted with DPAPI for the current Windows user.</summary>
    public string ProtectedToken { get; set; } = "";

    [JsonIgnore]
    public string Token
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedToken)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), Entropy, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { return ""; }
        }
        set => ProtectedToken = string.IsNullOrEmpty(value)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CherBot.Desktop.v1");

    public override string ToString() => $"{Name} ({Url})";
}

/// <summary>Stored in %AppData%\CherBot\desktop.json. Nothing about the bot itself lives on this side.</summary>
public sealed class DesktopSettings
{
    public const string LocalMode = "local", RemoteMode = "remote";

    /// <summary>"local", "remote", or empty before the first-run choice.</summary>
    public string Mode { get; set; } = "";
    public List<RemoteProfile> Profiles { get; set; } = new();
    public string? ActiveProfile { get; set; }
    /// <summary>Optional data folder for Local mode; defaults to the engine's normal Windows location.</summary>
    public string? LocalDataDir { get; set; }
    public int LocalApiPort { get; set; } = 8091;
    public int LocalWebSocketPort { get; set; } = 8080;

    /// <summary>%AppData%\CherBot, or CHERBOT_DESKTOP_DIR when set (portable installs and testing).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("CHERBOT_DESKTOP_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CherBot");
    private static string FilePath => Path.Combine(Folder, "desktop.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), Json) ?? new DesktopSettings();
        }
        catch (JsonException) { /* start fresh */ }
        return new DesktopSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public RemoteProfile? Active => Profiles.Find(p => p.Name == ActiveProfile) ?? (Profiles.Count > 0 ? Profiles[0] : null);
}
