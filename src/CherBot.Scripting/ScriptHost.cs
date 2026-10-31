using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Model;
using Microsoft.Extensions.Logging;
using Streamer.bot.Plugin.Interface;

namespace CherBot.Scripting;

public sealed class ScriptOptions
{
    /// <summary>Folder for extra assemblies scripts may reference by file name ({dataDir}/references by default).</summary>
    public string? ReferencesDir { get; set; }
}

/// <summary>
/// Compiles, caches and runs C# scripts. One instance per distinct script source; Init runs once, Execute per run, and runs
/// of the same instance are serialised because the instance holds the current arguments. Scripts run in-process with full
/// trust, exactly as in Streamer.bot: only import code you trust.
/// </summary>
public sealed class ScriptHost(ScriptCompiler compiler, ScriptOptions options, ConfigService config, IServiceProvider services, ILogger<ScriptHost> log) : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Compiled>> _cache = new();

    internal sealed class Compiled(CPHInlineBase instance, ScriptLoadContext context)
    {
        public CPHInlineBase Instance { get; } = instance;
        public ScriptLoadContext Context { get; } = context;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Initialized { get; set; }

        /// <summary>
        /// Streamer.bot scripts declare <c>public bool Execute()</c> without <c>override</c>, which hides the base method,
        /// so script methods are looked up on the script's own type rather than called virtually.
        /// </summary>
        public object? Call(string name)
        {
            var type = Instance.GetType();
            var mi = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, Type.EmptyTypes)
                     ?? type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
                     ?? throw new MissingMethodException("CPHInline", name);
            try { return mi.Invoke(Instance, null); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        public bool HasMethod(string name) =>
            Instance.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null;
    }

    /// <summary>Script source from a sub-action: Streamer.bot's base64 <c>byteCode</c>, or CherBot's plain <c>code</c> field.</summary>
    public static string SourceOf(SubActionDef sa)
    {
        if (sa.Has("code")) return sa.Str("code");
        var b64 = sa.Str("byteCode");
        if (string.IsNullOrWhiteSpace(b64)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { return b64; }
    }

    private IReadOnlyList<string> ReferencePaths(SubActionDef sa)
    {
        var dir = options.ReferencesDir;
        var list = new List<string>();
        foreach (var r in sa.StrList("references"))
        {
            var file = Path.GetFileName(r.Replace('\\', '/'));
            // .NET Framework system assemblies are covered by the .NET 8 reference set.
            if (file.StartsWith("System", StringComparison.OrdinalIgnoreCase) || file.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                file.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                file.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Streamer.bot", StringComparison.OrdinalIgnoreCase))
                continue;
            var candidate = dir is null ? null : Path.Combine(dir, file);
            if (candidate is not null && File.Exists(candidate)) list.Add(candidate);
            else if (File.Exists(r)) list.Add(r);
            else log.LogWarning("Script reference {Reference} not found; put it in {Dir}", r, dir);
        }
        return list;
    }

    internal Compiled Get(SubActionDef sa)
    {
        var source = SourceOf(sa);
        var refs = ReferencePaths(sa);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + string.Join("|", refs))))[..32];
        var lazy = _cache.GetOrAdd(key, k => new Lazy<Compiled>(() =>
        {
            var (type, ctx) = compiler.Compile(source, refs, k);
            log.LogInformation("Compiled C# script {Name} ({Key})", sa.Str("name") is { Length: > 0 } n ? n : sa.Id.ToString(), k[..8]);
            return new Compiled((CPHInlineBase)Activator.CreateInstance(type)!, ctx);
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value; }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>Runs Execute (or a named method). Returns the script's boolean result.</summary>
    public async Task<bool> RunAsync(SubActionDef sa, RunContext ctx, string? method = null)
    {
        var compiled = Get(sa);
        await compiled.Gate.WaitAsync(ctx.Cancellation);
        try
        {
            return await Task.Run(() =>
            {
                var proxy = new CphProxy(ctx, services);
                compiled.Instance.___init(proxy, ctx.Args.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase));
                if (!compiled.Initialized)
                {
                    compiled.Call("Init");
                    compiled.Initialized = true;
                }
                var result = compiled.Call(method ?? "Execute");
                return result is not bool b || b;
            }, ctx.Cancellation);
        }
        finally
        {
            compiled.Gate.Release();
        }
    }

    /// <summary>Finds an Execute C# Code sub-action by id in any action (for Execute C# Method).</summary>
    public SubActionDef? FindCode(Guid id) =>
        config.Actions.SelectMany(a => a.AllSubActions()).FirstOrDefault(s => s.Id == id && s.Type == 99999);

    public SubActionDef? FindCodeByName(string name) =>
        config.Actions.SelectMany(a => a.AllSubActions())
            .FirstOrDefault(s => s.Type == 99999 && (string.Equals(s.Str("name"), name, StringComparison.OrdinalIgnoreCase) || s.Id.ToString() == name));

    /// <summary>Compiles ahead of time and runs Init (the "precompile" option).</summary>
    public void Precompile(SubActionDef sa)
    {
        var compiled = Get(sa);
        if (compiled.Initialized) return;
        compiled.Gate.Wait();
        try
        {
            if (compiled.Initialized) return;
            var ctx = new RunContext(Guid.NewGuid(), new ActionDef { Name = "precompile" }, new Dictionary<string, object?>(), services, log, CancellationToken.None);
            compiled.Instance.___init(new CphProxy(ctx, services), new Dictionary<string, object>());
            compiled.Call("Init");
            compiled.Initialized = true;
        }
        finally { compiled.Gate.Release(); }
    }

    public int CachedCount => _cache.Count;

    public void Dispose()
    {
        foreach (var lazy in _cache.Values.Where(l => l.IsValueCreated))
        {
            try { lazy.Value.Call("Dispose"); } catch (Exception ex) { log.LogDebug(ex, "Script Dispose failed"); }
            lazy.Value.Context.Unload();
        }
        _cache.Clear();
    }
}
