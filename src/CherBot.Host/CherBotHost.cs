using System.Net;
using System.Reflection;
using CherBot.Core;
using CherBot.Core.Platform;
using CherBot.Host.Api;
using CherBot.Host.Platform;
using CherBot.Host.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Serilog.Events;

namespace CherBot.Host;

/// <summary>Builds the CherBot engine host. Used by the console/service entry point and by the Windows GUI (Local mode).</summary>
public static class CherBotHost
{
    public static string Version { get; } =
        typeof(CherBotHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <param name="args">Command-line arguments (supports --data-dir and any CherBot:* configuration key).</param>
    /// <param name="configure">Extra service registrations, for example desktop services from the Windows GUI.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var dataDir = DataDirectory.Resolve(args);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration
            .AddJsonFile(Path.Combine(dataDir, "cherbot.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("CHERBOT_")
            .AddCommandLine(args);

        var options = builder.Configuration.GetSection(CherBotHostOptions.Section).Get<CherBotHostOptions>() ?? new CherBotHostOptions();
        options.DataDir = dataDir;
        Validate(options);

        builder.Host.UseWindowsService(o => o.ServiceName = "CherBot");
        builder.Host.UseSystemd();

        builder.Host.UseSerilog((ctx, _, log) => log
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext:l}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(dataDir, "logs", "cherbot-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext:l}: {Message:lj}{NewLine}{Exception}"));

        builder.WebHost.ConfigureKestrel(k =>
        {
            Listen(k, options.Api);
            if (options.WebSocketServer.Enabled) Listen(k, options.WebSocketServer);
            if (options.HttpServer.Enabled) Listen(k, options.HttpServer);
        });

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(options.Engine);
        services.AddSingleton(ApiToken.Load(dataDir, options.Api.Token));

        var dp = services.AddDataProtection()
            .SetApplicationName("CherBot")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
        if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi();
        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        services.AddSingleton<CommandLineSoundPlayer>();
        services.AddSingleton<OverlaySoundPlayer>();
        services.AddSingleton<ISoundPlayer, SoundRouter>();

        services.AddCherBotCore(Path.Combine(dataDir, "cherbot.db"), options.Engine);
        HostModules.Register(builder, options);

        configure?.Invoke(builder);

        var app = builder.Build();
        SecureKeyFiles(dataDir);
        HostModules.Map(app, options);
        MapStatic(app);
        return app;
    }

    private static void Validate(CherBotHostOptions o)
    {
        var ports = new List<(string, int)> { ("Api", o.Api.Port) };
        if (o.WebSocketServer.Enabled) ports.Add(("WebSocketServer", o.WebSocketServer.Port));
        if (o.HttpServer.Enabled) ports.Add(("HttpServer", o.HttpServer.Port));
        var dupes = ports.GroupBy(p => p.Item2).Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
            throw new InvalidOperationException($"Port {dupes[0].Key} is used by more than one listener: {string.Join(", ", dupes[0].Select(p => p.Item1))}");
    }

    private static void Listen(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions k, ListenerOptions l)
    {
        switch (l.Address.Trim())
        {
            case "localhost":
                k.ListenLocalhost(l.Port);
                break;
            case "*" or "0.0.0.0" or "":
                k.ListenAnyIP(l.Port);
                break;
            default:
                k.Listen(IPAddress.Parse(l.Address), l.Port);
                break;
        }
    }

    private static void SecureKeyFiles(string dataDir)
    {
        if (OperatingSystem.IsWindows()) return;
        var keys = Path.Combine(dataDir, "keys");
        if (!Directory.Exists(keys)) return;
        try
        {
            File.SetUnixFileMode(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var f in Directory.EnumerateFiles(keys)) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            foreach (var f in Directory.EnumerateFiles(dataDir, "cherbot.db*")) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort */ }
    }

    private static void MapStatic(WebApplication app)
    {
        // CHERBOT_WEBUI_DIR serves the UI from disk while developing it; the embedded copy is used otherwise.
        IFileProvider files = new ManifestEmbeddedFileProvider(typeof(CherBotHost).Assembly, "webui");
        if (Environment.GetEnvironmentVariable("CHERBOT_WEBUI_DIR") is { Length: > 0 } dir && Directory.Exists(dir))
            files = new CompositeFileProvider(new PhysicalFileProvider(Path.GetFullPath(dir)), files);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        // Revalidate on every load so an upgraded engine never runs with stale UI modules from the browser cache.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });
    }
}
