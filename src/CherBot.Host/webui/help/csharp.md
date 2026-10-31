# C# scripts

The **Execute C# Code** step runs C# with Streamer.bot's `CPH` API, so most existing Streamer.bot scripts work unchanged.

```csharp
public class CPHInline
{
    public bool Execute()
    {
        var user = args["user"].ToString();
        CPH.SendMessage($"Hello {user}!");
        CPH.SetGlobalVar("lastGreeted", user, true);
        return true;
    }
}
```

- `args` holds the run's arguments; `CPH.SetArgument` adds new ones for later steps.
- Return `false` to stop the action.
- **Execute C# Method** calls another public method of a code step.
- Compile errors and exceptions appear in the step's trace (use **Save & Test**) and in the engine log.

Scripts run inside CherBot with full access to the engine machine, so only use code you trust. See the [operating guide](docs/operating-guide.md#15-c-scripts) for the supported `CPH` methods and references.
