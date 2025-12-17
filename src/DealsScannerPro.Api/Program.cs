using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DealsScannerPro.Api.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Register services
        services.AddSingleton<ITableStorageService, TableStorageService>();
        services.AddSingleton<IFuzzySearchService, FuzzySearchService>();
        services.AddSingleton<ICategoryService, CategoryService>();
    })
    .Build();

host.Run();
