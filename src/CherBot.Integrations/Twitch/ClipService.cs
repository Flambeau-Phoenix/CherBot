using CherBot.Integrations.Community;

namespace CherBot.Integrations.Twitch;

public sealed record ClipResult(string Id, string Url, string? EditUrl);

public sealed class ClipService(TwitchAuth auth, HelixClient helix, StreamSessionService sessions)
{
    public async Task<ClipResult> CreateAsync(CancellationToken ct = default)
    {
        var token = auth.Get(TwitchRole.Broadcaster) ?? throw new InvalidOperationException("Link the broadcaster account first.");
        if (!token.Scopes.Contains("clips:edit")) throw new InvalidOperationException("Relink Twitch with the clips:edit permission.");
        await sessions.ReconcileAsync(ct);
        if (!sessions.Live || sessions.LastError is not null) throw new InvalidOperationException("Clips require a confirmed live stream.");
        var result = HelixClient.First(await helix.SendAsync(HttpMethod.Post, "clips",
            new Dictionary<string, string?> { ["broadcaster_id"] = token.UserId }, ct: ct));
        if (result is null) throw new InvalidOperationException("Twitch did not accept the clip request. Check channel clip settings.");
        var id = result.Value.GetProperty("id").GetString()!;
        var edit = result.Value.TryGetProperty("edit_url", out var url) ? url.GetString() : null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        do
        {
            if (HelixClient.First(await helix.GetAsync("clips", new Dictionary<string, string?> { ["id"] = id }, ct: ct)) is { } clip)
                return new(id, clip.TryGetProperty("url", out var publicUrl) ? publicUrl.GetString()! : $"https://clips.twitch.tv/{id}", edit);
            await Task.Delay(1000, ct);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new InvalidOperationException("Twitch did not finish creating the clip within 15 seconds. Try again later.");
    }
}
