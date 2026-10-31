using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Scripting;

/// <summary>Execute C# Code (99999) and Execute C# Method (99998). A script returning false stops the action, as in Streamer.bot.</summary>
public sealed class ScriptHandlers : SubActionHandlerGroup
{
    public ScriptHandlers()
    {
        On(99999, async (s, c) =>
        {
            var host = c.Service<ScriptHost>();
            bool result;
            try { result = await host.RunAsync(s, c); }
            catch (ScriptCompileException ex)
            {
                c.Log.LogError("C# code in '{Action}' does not compile:\n{Errors}", c.Action.Name, string.Join("\n", ex.Errors));
                throw;
            }
            if (s.Bool("saveResultToVariable")) c.SetArg(s.Str("saveToVariable") is { Length: > 0 } n ? n : "result", result);
            return result ? FlowSignal.Continue : FlowSignal.Break;
        });
        On(99998, async (s, c) =>
        {
            var host = c.Service<ScriptHost>();
            if (s.GuidOf("executeCodeId") is not { } id || host.FindCode(id) is not { } code)
            {
                c.Log.LogWarning("Execute C# Method: code sub-action {Id} not found", s.Str("executeCodeId"));
                return FlowSignal.Continue;
            }
            var result = await host.RunAsync(code, c, s.Str("method") is { Length: > 0 } m ? m : "Execute");
            if (s.Bool("saveResultToVariable")) c.SetArg(s.Str("saveToVariable") is { Length: > 0 } n ? n : "result", result);
            return result ? FlowSignal.Continue : FlowSignal.Break;
        });
    }
}

/// <summary>Compiles scripts marked "precompile" at startup and runs their Init.</summary>
public sealed class ScriptPrecompileService(ConfigService config, ScriptHost host, ILogger<ScriptPrecompileService> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        foreach (var sa in config.Actions.SelectMany(a => a.AllSubActions()).Where(s => s.Type == 99999 && s.Bool("precompile")))
        {
            if (stoppingToken.IsCancellationRequested) return;
            try { host.Precompile(sa); }
            catch (Exception ex) { log.LogWarning("Precompile of script {Id} failed: {Error}", sa.Id, ex.Message); }
        }
    }, stoppingToken);
}

public static class ScriptingServiceCollectionExtensions
{
    public static IServiceCollection AddScripting(this IServiceCollection services, ScriptOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<ScriptCompiler>();
        services.AddSingleton<ScriptHost>();
        services.AddSingleton<ISubActionHandler, ScriptHandlers>();
        services.AddHostedService<ScriptPrecompileService>();
        return services;
    }
}
