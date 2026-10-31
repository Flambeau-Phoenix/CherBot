using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Platform;

public sealed class EngineOptions
{
    /// <summary>Prefix rewrites applied to imported paths, for example "C:\\Stream\\Sounds" → "/srv/stream/sounds".</summary>
    public Dictionary<string, string> PathMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int RunHistoryDays { get; set; } = 14;
    public int EventLogDays { get; set; } = 3;
    public int PendingFlushSeconds { get; set; } = 30;
    /// <summary>Sound output: auto, local, overlay or none.</summary>
    public string SoundMode { get; set; } = "auto";
    /// <summary>Folders the overlay may stream sound files from.</summary>
    public List<string> MediaRoots { get; set; } = [];
}

public sealed class PathMapper(EngineOptions options) : IPathMapper
{
    public string Map(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var p = path.Trim().Trim('"');
        foreach (var (from, to) in options.PathMappings.OrderByDescending(kv => kv.Key.Length))
        {
            if (p.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                p = to + p[from.Length..];
                break;
            }
        }
        if (!OperatingSystem.IsWindows())
        {
            p = p.Replace('\\', '/');
            if (!File.Exists(p) && !Directory.Exists(p)) p = ResolveCaseInsensitive(p) ?? p;
        }
        return p;
    }

    /// <summary>Linux file systems are case-sensitive; Windows-authored paths often are not exact.</summary>
    private static string? ResolveCaseInsensitive(string path)
    {
        if (!Path.IsPathRooted(path)) return null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "/";
        foreach (var part in parts)
        {
            var exact = Path.Combine(current, part);
            if (File.Exists(exact) || Directory.Exists(exact)) { current = exact; continue; }
            if (!Directory.Exists(current)) return null;
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            current = match;
        }
        return current;
    }
}

public sealed class ProcessRunner(ILogger<ProcessRunner> log) : IProcessRunner
{
    public async Task<int?> RunAsync(string command, string arguments, string? workingDir, IReadOnlyDictionary<string, string>? env, int waitMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDir)) psi.WorkingDirectory = workingDir;
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows())
        {
            // Documents and URLs need the shell on Windows.
            log.LogDebug(ex, "Direct start failed, retrying with shell execute");
            psi.UseShellExecute = true;
            proc = Process.Start(psi);
        }
        if (proc is null) return null;
        if (waitMs <= 0) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(waitMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Plays audio by shelling out to a player found on PATH (pw-play, paplay, aplay, ffplay, mpv).</summary>
public sealed class CommandLineSoundPlayer : ISoundPlayer
{
    private static readonly string[] Candidates = ["pw-play", "paplay", "ffplay", "mpv", "aplay", "afplay"];
    private readonly ILogger<CommandLineSoundPlayer> _log;
    private readonly string? _player;
    private readonly List<Process> _running = [];

    public CommandLineSoundPlayer(ILogger<CommandLineSoundPlayer> log)
    {
        _log = log;
        _player = Candidates.FirstOrDefault(c => FindOnPath(c) is not null);
    }

    public string Name => _player is null ? "none" : $"cli:{_player}";
    public bool IsAvailable => _player is not null;

    public async Task PlayAsync(string file, float volume, bool wait, CancellationToken ct)
    {
        if (_player is null)
        {
            _log.LogWarning("No command-line audio player found on PATH; cannot play {File}", file);
            return;
        }
        var vol = Math.Clamp(volume, 0f, 1f);
        var args = _player switch
        {
            "pw-play" => $"--volume={vol.ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{file}\"",
            "paplay" => $"--volume={(int)(vol * 65536)} \"{file}\"",
            "ffplay" => $"-nodisp -autoexit -loglevel quiet -volume {(int)(vol * 100)} \"{file}\"",
            "mpv" => $"--no-video --really-quiet --volume={(int)(vol * 100)} \"{file}\"",
            "afplay" => $"-v {vol.ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{file}\"",
            _ => $"-q \"{file}\"",
        };
        var proc = Process.Start(new ProcessStartInfo(_player, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true });
        if (proc is null) return;
        lock (_running) _running.Add(proc);
        var done = proc.WaitForExitAsync(ct).ContinueWith(_ => { lock (_running) _running.Remove(proc); }, TaskScheduler.Default);
        if (wait) await done;
    }

    public void StopAll()
    {
        lock (_running)
        {
            foreach (var p in _running)
                try { if (!p.HasExited) p.Kill(); } catch { /* already gone */ }
            _running.Clear();
        }
    }

    internal static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
            if (OperatingSystem.IsWindows() && File.Exists(full + ".exe")) return full + ".exe";
        }
        return null;
    }
}
