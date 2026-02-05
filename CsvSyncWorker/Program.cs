using CsvSyncWorker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "CsvSyncService";
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<CsvWorker>();

        services.AddHttpClient<BackendClient>((serviceProvider, client) =>
        {
            var config = hostContext.Configuration;
            var baseUrl = config["Backend:BaseUrl"] ?? "http://localhost:8089";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
    })
    .Build();

await host.RunAsync();

