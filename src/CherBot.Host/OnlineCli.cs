using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CherBot.Host;

/// <summary>Talks to the running engine; never opens its SQLite database or prints authentication material.</summary>
internal static class OnlineCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            string? Option(string name)
            {
                var i = Array.IndexOf(args, name);
                if (i < 0) return null;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException($"{name} needs a value.");
                return args[i + 1];
            }
            var url = Option("--url") ?? "http://127.0.0.1:8091";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo)) throw new ArgumentException("--url must be an HTTP(S) engine address.");
            var tokenFile = Option("--token-file") ?? Path.Combine(DataDirectory.ResolvePath(args), "api-token");
            if (!File.Exists(tokenFile)) throw new ArgumentException("Token file not found. Use --token-file for the target engine.");
            var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
            if (token.Length == 0) throw new ArgumentException("Token file is empty.");
            string method, path; string? body = null;
            if (args[0] == "api")
            {
                if (args.Length < 3) throw new ArgumentException("Usage: cherbot api <METHOD> </v1/path> [--body-file file] [--url URL] [--token-file file]");
                method = args[1].ToUpperInvariant(); path = args[2];
                if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) throw new ArgumentException("Unsupported HTTP method.");
                if (Option("--body-file") is { } file) body = await File.ReadAllTextAsync(file);
            }
            else
            {
                if (args.Length < 3 || args[1] is not ("export" or "preview" or "apply")) throw new ArgumentException("Usage: cherbot pack export|preview|apply <file> [--selection-file file] [--replace] [--mappings-file file] [--url URL] [--token-file file]");
                method = "POST"; path = "/v1/packs/" + args[1];
                if (args[1] == "export")
                {
                    if (Option("--selection-file") is { } selection) body = await File.ReadAllTextAsync(selection);
                    else throw new ArgumentException("Export needs --selection-file with actions/settings/globals to include.");
                }
                else
                {
                    var pack = JsonNode.Parse(await File.ReadAllTextAsync(args[2])) ?? throw new ArgumentException("Pack is empty.");
                    var mappings = Option("--mappings-file") is { } mappingsFile ? JsonNode.Parse(await File.ReadAllTextAsync(mappingsFile)) : null;
                    body = new JsonObject { ["pack"] = pack, ["replace"] = args.Contains("--replace"), ["mappings"] = mappings }.ToJsonString();
                }
            }
            if (!path.StartsWith("/v1/", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('#') || path.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Path must be an engine /v1/ route.");
            var target = new Uri(baseUri, path);
            if (target.Host != baseUri.Host || target.Port != baseUri.Port) throw new ArgumentException("API requests must stay on the selected engine.");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) { Console.Error.WriteLine($"HTTP {(int)response.StatusCode}: {text}"); return 1; }
            if (args[0] == "pack" && args[1] == "export") { await File.WriteAllTextAsync(args[2], text); Console.WriteLine($"Exported {args[2]}"); }
            else Console.WriteLine(text);
            if (args[0] == "pack" && args[1] == "preview" && JsonNode.Parse(text)?["canApply"]?.GetValue<bool>() == false) return 3;
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or HttpRequestException or JsonException or TaskCanceledException)
        { Console.Error.WriteLine(ex.Message); return 2; }
    }
}
