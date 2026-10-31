using CherBot.Compat.Import;
using CherBot.Compat.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace CherBot.Compat;

public static class CompatServiceCollectionExtensions
{
    public static IServiceCollection AddStreamerBotCompat(this IServiceCollection services, SbServerSettings settings, UdpServerSettings udp)
    {
        services.AddSingleton(settings);
        services.AddSingleton(udp);
        services.AddSingleton<StreamerBotApi>();
        services.AddSingleton<StreamerBotWebSocketServer>();
        services.AddSingleton<CherBot.Core.Platform.IWebSocketBroadcaster>(sp => sp.GetRequiredService<StreamerBotWebSocketServer>());
        services.AddSingleton<StreamerBotHttpServer>();
        services.AddHostedService<StreamerBotUdpServer>();
        services.AddSingleton<StreamerBotImporter>();
        return services;
    }
}
