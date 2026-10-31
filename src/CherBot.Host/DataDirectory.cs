namespace CherBot.Host;

/// <summary>
/// Resolves the data directory: --data-dir, then CHERBOT_DATA, then systemd's STATE_DIRECTORY, then the OS default
/// (%ProgramData%\CherBot on Windows; /var/lib/cherbot for root or $XDG_DATA_HOME/cherbot otherwise on Linux).
/// </summary>
public static class DataDirectory
{
    public static string Resolve(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--data-dir" or "--datadir" && i + 1 < args.Length) return Ensure(args[i + 1]);
            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal)) return Ensure(args[i]["--data-dir=".Length..]);
        }
        if (Environment.GetEnvironmentVariable("CHERBOT_DATA") is { Length: > 0 } env) return Ensure(env);
        if (Environment.GetEnvironmentVariable("STATE_DIRECTORY") is { Length: > 0 } state) return Ensure(state.Split(':')[0]);
        return Ensure(Default());
    }

    public static string Default()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CherBot");
        if (Environment.UserName == "root") return "/var/lib/cherbot";
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdg, "cherbot");
    }

    private static string Ensure(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        Directory.CreateDirectory(full);
        Directory.CreateDirectory(Path.Combine(full, "logs"));
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* not the owner; leave as is */ }
        }
        var config = Path.Combine(full, "cherbot.json");
        if (!File.Exists(config))
        {
            using var template = typeof(DataDirectory).Assembly.GetManifestResourceStream("CherBot.DefaultConfig.json")
                ?? throw new InvalidOperationException("The bundled CherBot configuration template is missing.");
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            FileStream output;
            try { output = new FileStream(config, fileOptions); }
            catch (IOException) when (File.Exists(config))
            {
                return full; // another first-run process created it
            }
            using (output) template.CopyTo(output);
        }
        return full;
    }

    /// <summary>Writes a private file (0600 on Unix).</summary>
    public static void WritePrivate(string path, string contents)
    {
        if (!OperatingSystem.IsWindows())
        {
            using var fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var w = new StreamWriter(fs);
            w.Write(contents);
            return;
        }
        File.WriteAllText(path, contents);
    }
}
