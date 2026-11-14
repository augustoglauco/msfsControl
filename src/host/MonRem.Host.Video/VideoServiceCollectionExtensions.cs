using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MonRem.Host.Video;

public static class VideoServiceCollectionExtensions
{
    /// <summary>Registra as opções e, só com <c>MonRem:Video:Enabled=true</c>, o serviço de vídeo.</summary>
    public static IServiceCollection AddMonRemVideo(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(VideoOptions.Section);
        services.Configure<VideoOptions>(section);
        if (section.Get<VideoOptions>()?.Enabled == true)
            services.AddHostedService<VideoHostedService>();
        return services;
    }
}
