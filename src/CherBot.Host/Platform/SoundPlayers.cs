using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CherBot.Core.Events;
using CherBot.Core.Platform;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CherBot.Host.Platform;

/// <summary>Plays WAV, AIFF and MP3 through WaveOut on Windows; other formats fall back to a command-line player when one exists.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSoundPlayer(CommandLineSoundPlayer fallback, ILogger<WindowsSoundPlayer> log) : ISoundPlayer
{
    private readonly ConcurrentDictionary<Guid, WaveOutEvent> _playing = new();

    public string Name => "naudio";

    public async Task PlayAsync(string file, float volume, bool wait, CancellationToken ct)
    {
        WaveStream reader;
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".wav": reader = new WaveFileReader(file); break;
            case ".aif" or ".aiff": reader = new AiffFileReader(file); break;
            case ".mp3": reader = new Mp3FileReaderBase(file, wf => new AcmMp3FrameDecompressor(wf)); break;
            default:
                if (fallback.IsAvailable) { await fallback.PlayAsync(file, volume, wait, ct); return; }
                log.LogWarning("Unsupported audio format for {File}; install ffplay or convert to WAV/MP3", file);
                return;
        }

        var output = new WaveOutEvent();
        var id = Guid.NewGuid();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) =>
        {
            _playing.TryRemove(id, out _);
            output.Dispose();
            reader.Dispose();
            done.TrySetResult();
        };
        output.Init(new SampleChannel(reader, true) { Volume = Math.Clamp(volume, 0f, 1f) });
        _playing[id] = output;
        output.Play();
        if (wait)
        {
            using var reg = ct.Register(() => output.Stop());
            await done.Task;
        }
    }

    public void StopAll()
    {
        foreach (var o in _playing.Values) o.Stop();
        fallback.StopAll();
    }
}

/// <summary>Sends sounds to browser overlays (/overlay) over the event stream; suits a headless box with no audio device.</summary>
public sealed class OverlaySoundPlayer(EventBus bus) : ISoundPlayer
{
    public string Name => "overlay";

    public async Task PlayAsync(string file, float volume, bool wait, CancellationToken ct)
    {
        bus.Publish(new BotEvent("CherBot", "PlaySound", new Dictionary<string, object?>
        {
            ["file"] = file,
            ["url"] = "/v1/media?path=" + Uri.EscapeDataString(file),
            ["volume"] = volume,
        }) { Persist = false });
        if (wait)
        {
            // The overlay plays asynchronously; approximate "finish before continuing" with the file's duration when known.
            var duration = TryDuration(file);
            if (duration > TimeSpan.Zero) await Task.Delay(duration, ct);
        }
    }

    public void StopAll() => bus.Publish(new BotEvent("CherBot", "StopSounds") { Persist = false });

    private static TimeSpan TryDuration(string file)
    {
        try
        {
            return Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".wav" => new WaveFileReader(file).Using(r => r.TotalTime),
                ".mp3" => new Mp3FileReaderBase(file, _ => null!).Using(r => r.TotalTime),
                _ => TimeSpan.Zero,
            };
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}

internal static class DisposableExtensions
{
    public static TResult Using<T, TResult>(this T disposable, Func<T, TResult> f) where T : IDisposable
    {
        using (disposable) return f(disposable);
    }
}

/// <summary>Picks the sound output from Engine:SoundMode (auto, local, overlay, none).</summary>
public sealed class SoundRouter : ISoundPlayer
{
    private readonly ISoundPlayer? _player;

    public SoundRouter(EngineOptions options, IServiceProvider sp, ILogger<SoundRouter> log)
    {
        var cli = sp.GetRequiredService<CommandLineSoundPlayer>();
        ISoundPlayer? local = OperatingSystem.IsWindows() ? new WindowsSoundPlayer(cli, sp.GetRequiredService<ILogger<WindowsSoundPlayer>>()) : cli.IsAvailable ? cli : null;
        var overlay = sp.GetRequiredService<OverlaySoundPlayer>();
        _player = options.SoundMode.ToLowerInvariant() switch
        {
            "none" => null,
            "overlay" => overlay,
            "local" => local,
            _ => local ?? overlay,
        };
        log.LogInformation("Sound output: {Player}", _player?.Name ?? "none");
    }

    public string Name => _player?.Name ?? "none";

    public Task PlayAsync(string file, float volume, bool wait, CancellationToken ct) =>
        _player?.PlayAsync(file, volume, wait, ct) ?? Task.CompletedTask;

    public void StopAll() => _player?.StopAll();
}
