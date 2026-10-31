using CherBot.Compat.Import;
using CherBot.Core.Config;

namespace CherBot.Tests;

public class DocumentationTests
{
    [Fact]
    public async Task Ai_example_import_has_supported_steps_and_a_linked_command()
    {
        await using var host = new TestHost();
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "docs", "hello-workflow.json"));
        var importer = host.Get<StreamerBotImporter>();
        var preview = importer.Import(text, new ImportOptions { DryRun = true });
        Assert.Equal(1, preview.Actions);
        Assert.Equal(1, preview.Commands);
        Assert.Empty(preview.Unsupported);
        Assert.Empty(preview.Warnings);

        importer.Import(text);
        var config = host.Get<ConfigService>();
        var command = Assert.Single(config.Commands.Where(c => c.Name == "AI example: hello"));
        var action = Assert.Single(config.Actions.Where(a => a.Name == "AI example: greet viewer"));
        Assert.Equal(command.Id, Assert.Single(action.Triggers).GuidOf("commandId"));
        Assert.Equal(new[] { 123, 10 }, action.SubActions.Select(s => s.Type));
    }
}
