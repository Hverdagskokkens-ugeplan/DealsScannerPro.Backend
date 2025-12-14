using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Models;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class UploadDeals
{
    private readonly ITableStorageService _storageService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UploadDeals> _logger;

    public UploadDeals(
        ITableStorageService storageService,
        IConfiguration configuration,
        ILogger<UploadDeals> logger)
    {
        _storageService = storageService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("UploadDeals")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/upload")] HttpRequestData req)
    {
        // Validate API key
        var expectedApiKey = _configuration["AdminApiKey"];
        var providedApiKey = req.Headers.TryGetValues("x-api-key", out var keys)
            ? keys.FirstOrDefault()
            : null;

        if (string.IsNullOrEmpty(providedApiKey) || providedApiKey != expectedApiKey)
        {
            _logger.LogWarning("Unauthorized upload attempt");
            var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauthorizedResponse.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
            return unauthorizedResponse;
        }

        try
        {
            var request = await req.ReadFromJsonAsync<UploadRequest>();

            if (request == null || request.Tilbud.Count == 0)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(new { error = "Invalid request body or no tilbud provided" });
                return badRequestResponse;
            }

            var count = await _storageService.UploadTilbudAsync(request);

            _logger.LogInformation("Successfully uploaded {Count} tilbud from {Source}",
                count, request.Meta.KildeFil);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                tilbud_imported = count,
                butik = request.Meta.Butik,
                periode = $"{request.Meta.GyldigFra} - {request.Meta.GyldigTil}"
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading deals");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to upload deals", details = ex.Message });
            return errorResponse;
        }
    }
}
