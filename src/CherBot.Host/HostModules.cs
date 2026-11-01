using CherBot.Compat;
using CherBot.Compat.Protocol;
using CherBot.Host.Api;
using CherBot.Integrations;
using CherBot.Integrations.Obs;
using CherBot.Integrations.SpeakerBot;
using CherBot.Integrations.Twitch;
using CherBot.Scripting;

namespace CherBot.Host;

/// <summary>Registers and maps the host's feature modules (management API, Streamer.bot-compatible servers, integrations, scripting).</summary>
internal static class HostModules
{
    public static void Register(WebApplicationBuilder builder, CherBotHostOptions options)
    {
        builder.Services.AddStreamerBotCompat(
            new SbServerSettings
            {
                InstanceName = options.InstanceName,
                Version = CherBotHost.Version,
                Endpoint = options.WebSocketServer.Endpoint,
                Password = options.WebSocketServer.Password,
                EnforceAuth = options.WebSocketServer.EnforceAuth,
            },
            new UdpServerSettings
            {
                Enabled = options.UdpServer.Enabled,
                Address = options.UdpServer.Address,
                Port = options.UdpServer.Port,
            });

        var config = builder.Configuration;
        builder.Services.AddSingleton<CherBot.Host.Sharing.FeaturePackService>();
        builder.Services.AddTwitch(
            config.GetSection(TwitchOptions.Section).Get<TwitchOptions>() ?? new TwitchOptions(),
            config.GetSection(CommunityOptions.Section).Get<CommunityOptions>() ?? new CommunityOptions());
        builder.Services.AddObs(config.GetSection(ObsOptions.Section).Get<ObsOptions>() ?? new ObsOptions());
        builder.Services.AddSpeakerBot(config.GetSection(SpeakerBotOptions.Section).Get<SpeakerBotOptions>() ?? new SpeakerBotOptions());

        var references = Path.Combine(options.DataDir!, "references");
        Directory.CreateDirectory(references);
        builder.Services.AddScripting(new ScriptOptions { ReferencesDir = references });
    }

    public static void Map(WebApplication app, CherBotHostOptions options)
    {
        // Each listener gets its own pipeline, selected by the local port the request arrived on.
        if (options.WebSocketServer.Enabled)
        {
            var port = options.WebSocketServer.Port;
            app.MapWhen(ctx => ctx.Connection.LocalPort == port, branch =>
            {
                branch.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
                branch.Run(ctx => ctx.RequestServices.GetRequiredService<StreamerBotWebSocketServer>().HandleAsync(ctx));
            });
        }
        if (options.HttpServer.Enabled)
        {
            var port = options.HttpServer.Port;
            app.MapWhen(ctx => ctx.Connection.LocalPort == port,
                branch => branch.Run(ctx => ctx.RequestServices.GetRequiredService<StreamerBotHttpServer>().HandleAsync(ctx)));
        }

        app.UseWebSockets();
        SystemEndpoints.Map(app, options);
        ManagementApi.Map(app);
        TwitchEndpoints.Map(app);
        IntegrationEndpoints.Map(app);
        LoyaltyEndpoints.Map(app);
        GamesEndpoints.Map(app);
        HelperEndpoints.Map(app);
        GiveawayEndpoints.Map(app);
        ModuleEndpoints.Map(app);
        CustomCommandEndpoints.Map(app);
        LiveEndpoints.Map(app);
        PackEndpoints.Map(app);
        SongEndpoints.Map(app);
    }
}
