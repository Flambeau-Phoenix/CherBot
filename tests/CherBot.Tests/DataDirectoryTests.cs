using System.Text.Json;
using CherBot.Host;

namespace CherBot.Tests;

public class DataDirectoryTests
{
    [Fact]
    public void First_run_creates_an_editable_config_and_preserves_user_changes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cherbot-tests", "config-" + Guid.NewGuid().ToString("N"));
        try
        {
            DataDirectory.Resolve(["--data-dir", dir]);
            var config = Path.Combine(dir, "cherbot.json");
            Assert.True(File.Exists(config), "First run should create cherbot.json from the bundled template.");
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            Assert.Equal("", json.RootElement.GetProperty("CherBot").GetProperty("Twitch").GetProperty("ClientId").GetString());
            Assert.Equal("127.0.0.1", json.RootElement.GetProperty("CherBot").GetProperty("Api").GetProperty("Address").GetString());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));

            const string changed = "{\"CherBot\":{\"Twitch\":{\"ClientId\":\"my-client-id\"}}}";
            File.WriteAllText(config, changed);
            DataDirectory.Resolve(["--data-dir", dir]);
            Assert.Equal(changed, File.ReadAllText(config));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
