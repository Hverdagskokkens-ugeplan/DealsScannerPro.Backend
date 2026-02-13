using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DealsScannerPro.Api.Middleware;
using DealsScannerPro.Api.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        builder.UseMiddleware<RateLimitingMiddleware>();
        builder.UseMiddleware<CacheControlMiddleware>();
    })
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Register services
        services.AddSingleton<ITableStorageService, TableStorageService>();
        services.AddSingleton<IFuzzySearchService, FuzzySearchService>();
        services.AddSingleton<ICategoryService, CategoryService>();
        services.AddSingleton<IOfferService, OfferService>();
        services.AddSingleton<IScanLogService, ScanLogService>();
        services.AddSingleton<IRetailerRuleService, RetailerRuleService>();
        services.AddSingleton<ISkuOverrideService, SkuOverrideService>();
        services.AddSingleton<IReprocessService, ReprocessService>();
        services.AddSingleton<IProcessingRunService, ProcessingRunService>();
        services.AddSingleton<IProductAliasService, ProductAliasService>();
        services.AddSingleton<ISystemSettingsService, SystemSettingsService>();
        services.AddSingleton<IArchiveService, ArchiveService>();
    })
    .Build();

host.Run();
