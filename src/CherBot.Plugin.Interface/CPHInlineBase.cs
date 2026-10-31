using System.Collections.Generic;

namespace Streamer.bot.Plugin.Interface;

/// <summary>
/// Base class for "Execute C# Code" scripts. A script declares <c>public class CPHInline</c> with <c>Execute()</c>;
/// CherBot adds this base class when the script does not, which makes <see cref="CPH"/> and <see cref="args"/> available.
/// </summary>
public class CPHInlineBase
{
    private IInlineInvokeProxy _cph = null!;
    private Dictionary<string, object> _args = new();

    public IInlineInvokeProxy CPH => _cph;

    /// <summary>A copy of the run's arguments. Use <c>CPH.SetArgument</c> to pass values on to later sub-actions.</summary>
    public Dictionary<string, object> args => _args;

    public void ___init(IInlineInvokeProxy cph, Dictionary<string, object> arguments)
    {
        _cph = cph;
        _args = arguments;
    }

    public virtual void Init() { }

    public virtual void Dispose() { }

    public virtual bool Execute() => false;
}
