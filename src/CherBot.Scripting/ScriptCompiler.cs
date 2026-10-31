using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Streamer.bot.Plugin.Interface;

namespace CherBot.Scripting;

public sealed class ScriptCompileException(IReadOnlyList<string> errors)
    : Exception("C# compile failed:\n" + string.Join("\n", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>A collectible load context per compiled script; framework and CherBot assemblies resolve from the default context.</summary>
internal sealed class ScriptLoadContext(string name, IReadOnlyList<string> referencePaths) : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = referencePaths.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

/// <summary>Compiles Streamer.bot-style "Execute C# Code" scripts with Roslyn.</summary>
public sealed class ScriptCompiler
{
    private static readonly string[] GlobalUsings =
    [
        "System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading", "System.Threading.Tasks",
        "System.IO", "System.Net.Http", "Streamer.bot.Plugin.Interface", "Streamer.bot.Plugin.Interface.Model",
        "Streamer.bot.Plugin.Interface.Enums", "Streamer.bot.Common.Events",
    ];

    private readonly Lazy<IReadOnlyList<MetadataReference>> _baseReferences = new(BuildBaseReferences);

    private static IReadOnlyList<MetadataReference> BuildBaseReferences()
    {
        var refs = new List<MetadataReference>(Basic.Reference.Assemblies.Net80.References.All);
        refs.Add(ReferenceFor(typeof(CPHInlineBase).Assembly));
        refs.Add(ReferenceFor(typeof(Newtonsoft.Json.JsonConvert).Assembly));
        return refs;
    }

    /// <summary>
    /// Reference for an assembly that ships with CherBot. Single-file publishes must use IncludeAllContentForSelfExtract so
    /// <see cref="Assembly.Location"/> is a real path; otherwise the file is looked up next to the executable.
    /// </summary>
    private static MetadataReference ReferenceFor(Assembly asm)
    {
        if (!string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location)) return MetadataReference.CreateFromFile(asm.Location);
        var beside = Path.Combine(AppContext.BaseDirectory, asm.GetName().Name + ".dll");
        if (File.Exists(beside)) return MetadataReference.CreateFromFile(beside);
        throw new InvalidOperationException($"Cannot locate {asm.GetName().Name}.dll for script compilation; publish with IncludeAllContentForSelfExtract=true.");
    }

    /// <summary>Adds <c>: CPHInlineBase</c> to <c>class CPHInline</c> when the script did not declare a base type.</summary>
    internal static SyntaxTree Prepare(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var root = tree.GetCompilationUnitRoot();
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == "CPHInline");
        if (cls is null || cls.BaseList is not null) return tree;
        var withBase = cls
            .WithIdentifier(cls.Identifier.WithTrailingTrivia(SyntaxFactory.Space))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("global::Streamer.bot.Plugin.Interface.CPHInlineBase"))))
                .WithTrailingTrivia(cls.Identifier.TrailingTrivia));
        return tree.WithRootAndOptions(root.ReplaceNode(cls, withBase), tree.Options);
    }

    /// <summary>Compiles the script and returns its CPHInline type in a fresh collectible context.</summary>
    internal (Type Type, ScriptLoadContext Context) Compile(string source, IReadOnlyList<string> referencePaths, string name)
    {
        var usings = CSharpSyntaxTree.ParseText(string.Join("\n", GlobalUsings.Select(u => $"global using {u};")), new CSharpParseOptions(LanguageVersion.Latest));
        var references = new List<MetadataReference>(_baseReferences.Value);
        foreach (var path in referencePaths) references.Add(MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "CherBotScript_" + name,
            [usings, Prepare(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable, allowUnsafe: false));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Location.GetLineSpan().StartLinePosition.Line - 0}:{d.Id}: {d.GetMessage()}").Take(20).ToList();
            throw new ScriptCompileException(errors);
        }
        ms.Position = 0;
        var ctx = new ScriptLoadContext(name, referencePaths);
        var asm = ctx.LoadFromStream(ms);
        var type = asm.GetTypes().FirstOrDefault(t => t.Name == "CPHInline" && typeof(CPHInlineBase).IsAssignableFrom(t))
                   ?? throw new ScriptCompileException(["The script must declare 'public class CPHInline' with 'public bool Execute()'."]);
        return (type, ctx);
    }
}
