using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MonRem.Host.Video;

namespace MonRem.Tests.Video;

public class VideoOptionsTests
{
    private static ServiceCollection Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddMonRemVideo(configuration);
        return services;
    }

    [Fact]
    public void Options_default_to_disabled_on_28252()
    {
        var options = new VideoOptions();

        Assert.False(options.Enabled);
        Assert.Equal(28252, options.TcpPort);
        Assert.Equal(28252, options.UdpPort);
        Assert.Equal(30, options.Fps);
    }

    [Fact]
    public void Disabled_registers_no_hosted_service()
    {
        var services = Services([]);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task Enabled_binds_from_configuration_and_runs()
    {
        var services = Services(new()
        {
            ["MonRem:Video:Enabled"] = "true",
            ["MonRem:Video:TcpPort"] = "0",
            ["MonRem:Video:UdpPort"] = "0",
            ["MonRem:Video:Fps"] = "25",
        });
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<VideoOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(0, options.TcpPort);
        Assert.Equal(25, options.Fps);

        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Port_in_use_does_not_throw_out_of_the_hosted_service()
    {
        using var occupier = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        occupier.Start();
        var port = ((System.Net.IPEndPoint)occupier.LocalEndpoint).Port;
        var services = Services(new()
        {
            ["MonRem:Video:Enabled"] = "true",
            ["MonRem:Video:TcpPort"] = port.ToString(),
            ["MonRem:Video:UdpPort"] = "0",
        });
        await using var provider = services.BuildServiceProvider();

        var hosted = (BackgroundService)Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        await hosted.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await hosted.StopAsync(CancellationToken.None);
    }
}
