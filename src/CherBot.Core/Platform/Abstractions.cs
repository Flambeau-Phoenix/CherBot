using System.Text.Json;

namespace CherBot.Core.Platform;

/// <summary>Plays audio files. Implementations: NAudio on Windows, a command-line player on Linux, or a browser overlay.</summary>
public interface ISoundPlayer
{
    string Name { get; }
    /// <summary>Plays <paramref name="file"/> at <paramref name="volume"/> (0..1). When <paramref name="wait"/> is true the task completes when playback ends.</summary>
    Task PlayAsync(string file, float volume, bool wait, CancellationToken ct);
    void StopAll();
}

public interface IProcessRunner
{
    /// <summary>Starts a program. Returns the exit code when <paramref name="waitMs"/> is positive and the process exits in time, otherwise null.</summary>
    Task<int?> RunAsync(string command, string arguments, string? workingDir, IReadOnlyDictionary<string, string>? env, int waitMs, CancellationToken ct);
}

/// <summary>Rewrites paths from imported Streamer.bot data (often Windows paths) to paths valid on this host.</summary>
public interface IPathMapper
{
    string Map(string path);
}

/// <summary>Features that need an interactive desktop session. Headless hosts register <see cref="NullDesktopServices"/>.</summary>
public interface IDesktopServices
{
    bool IsAvailable { get; }
    Task ShowToastAsync(string title, string text, string? iconPath, CancellationToken ct);
    Task<string?> GetInputAsync(string title, string prompt, string defaultText, CancellationToken ct);
    Task KeyPressAsync(JsonElement? key, JsonElement? modifiers, CancellationToken ct);
}

/// <summary>Encrypted secret storage (tokens, passwords).</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

/// <summary>Sends chat messages on one platform. Integrations register one per platform they support.</summary>
public interface IChatSender
{
    /// <summary>Lower-case platform name, for example "twitch".</summary>
    string Platform { get; }
    bool IsConnected { get; }
    Task<bool> SendMessageAsync(string text, bool useBot, string? replyToMessageId, CancellationToken ct);
}

public sealed class NullDesktopServices : IDesktopServices
{
    public bool IsAvailable => false;
    public Task ShowToastAsync(string title, string text, string? iconPath, CancellationToken ct) => Task.CompletedTask;
    public Task<string?> GetInputAsync(string title, string prompt, string defaultText, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task KeyPressAsync(JsonElement? key, JsonElement? modifiers, CancellationToken ct) => Task.CompletedTask;
}
