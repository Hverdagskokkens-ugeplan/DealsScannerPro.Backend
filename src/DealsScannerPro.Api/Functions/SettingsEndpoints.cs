using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class SettingsEndpoints
{
    private readonly ISystemSettingsService _settingsService;
    private readonly ILogger<SettingsEndpoints> _logger;

    public SettingsEndpoints(ISystemSettingsService settingsService, ILogger<SettingsEndpoints> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Get all settings.
    /// GET /api/settings
    /// </summary>
    [Function("GetSettings")]
    public async Task<HttpResponseData> GetSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings")] HttpRequestData req)
    {
        try
        {
            var settings = await _settingsService.GetAllSettingsAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                settings,
                count = settings.Count
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting settings");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Get a single setting by key.
    /// GET /api/settings/{key}
    /// </summary>
    [Function("GetSetting")]
    public async Task<HttpResponseData> GetSetting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/{key}")] HttpRequestData req,
        string key)
    {
        try
        {
            var value = await _settingsService.GetSettingAsync(key);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { key, value });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting setting {Key}", key);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Create or update a setting.
    /// PUT /api/settings/{key}
    /// </summary>
    [Function("UpsertSetting")]
    public async Task<HttpResponseData> UpsertSetting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/{key}")] HttpRequestData req,
        string key)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<UpsertSettingRequest>();

            if (request == null || string.IsNullOrWhiteSpace(request.Value))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "value is required" });
                return badRequest;
            }

            var setting = await _settingsService.UpsertSettingAsync(
                key, request.Value, request.Description, request.DataType, request.UpdatedBy);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                key = setting.RowKey,
                value = setting.Value,
                description = setting.Description,
                data_type = setting.DataType,
                updated_at = setting.UpdatedAt
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting setting {Key}", key);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Delete a setting.
    /// DELETE /api/settings/{key}
    /// </summary>
    [Function("DeleteSetting")]
    public async Task<HttpResponseData> DeleteSetting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "settings/{key}")] HttpRequestData req,
        string key)
    {
        try
        {
            await _settingsService.DeleteSettingAsync(key);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, message = $"Setting '{key}' deleted" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting setting {Key}", key);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Seed default settings.
    /// POST /api/management/seed-settings
    /// </summary>
    [Function("SeedSettings")]
    public async Task<HttpResponseData> SeedSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "management/seed-settings")] HttpRequestData req)
    {
        try
        {
            await _settingsService.SeedDefaultSettingsAsync();
            var settings = await _settingsService.GetAllSettingsAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                message = "Settings seeded successfully",
                count = settings.Count,
                settings
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding settings");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }
}

public class UpsertSettingRequest
{
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DataType { get; set; }
    public string? UpdatedBy { get; set; }
}
