using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Models;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

/// <summary>
/// CRUD API endpoints for retailer-specific scanning rules.
/// Rules are stored in Azure Table Storage (table: RetailerRules).
/// </summary>
public class RulesEndpoints
{
    private readonly IRetailerRuleService _ruleService;
    private readonly ILogger<RulesEndpoints> _logger;

    public RulesEndpoints(IRetailerRuleService ruleService, ILogger<RulesEndpoints> logger)
    {
        _ruleService = ruleService;
        _logger = logger;
    }

    /// <summary>
    /// List active rules for a retailer, optionally filtered by type.
    /// GET /api/rules/{retailer}?type=price_pattern
    /// </summary>
    [Function("GetRules")]
    public async Task<HttpResponseData> GetRules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/{retailer}")] HttpRequestData req,
        string retailer)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var ruleType = query["type"];

            var rules = await _ruleService.GetRulesAsync(retailer, ruleType);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                rules = rules.Select(MapRuleToResponse),
                count = rules.Count,
                retailer = retailer.ToLowerInvariant(),
                filter_type = ruleType
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting rules for {Retailer}", retailer);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Create a new retailer rule.
    /// POST /api/rules
    /// </summary>
    [Function("CreateRule")]
    public async Task<HttpResponseData> CreateRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules")] HttpRequestData req)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<CreateRuleRequest>();

            if (request == null
                || string.IsNullOrWhiteSpace(request.Retailer)
                || string.IsNullOrWhiteSpace(request.RuleType)
                || string.IsNullOrWhiteSpace(request.Pattern))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "retailer, rule_type, and pattern are required" });
                return badRequest;
            }

            var rule = new RetailerRule
            {
                PartitionKey = request.Retailer,
                RuleType = request.RuleType,
                Pattern = request.Pattern,
                Action = request.Action ?? string.Empty,
                TargetValue = request.TargetValue,
                CreatedFrom = request.CreatedFrom,
                CreatedBy = request.CreatedBy ?? "anonymous",
                Description = request.Description
            };

            var created = await _ruleService.CreateRuleAsync(rule);

            _logger.LogInformation("Rule created: {RowKey} for {Retailer}", created.RowKey, created.PartitionKey);

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                rule = MapRuleToResponse(created)
            });

            return response;
        }
        catch (ArgumentException ex)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = ex.Message });
            return badRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating rule");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Update an existing rule.
    /// PUT /api/rules/{retailer}/{id}
    /// </summary>
    [Function("UpdateRule")]
    public async Task<HttpResponseData> UpdateRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "rules/{retailer}/{id}")] HttpRequestData req,
        string retailer,
        string id)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<UpdateRuleRequest>();

            if (request == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Request body is required" });
                return badRequest;
            }

            var update = new RetailerRule
            {
                Pattern = request.Pattern ?? string.Empty,
                Action = request.Action ?? string.Empty,
                TargetValue = request.TargetValue,
                Description = request.Description
            };

            var updated = await _ruleService.UpdateRuleAsync(retailer, id, update);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                rule = MapRuleToResponse(updated)
            });

            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Rule not found: {Retailer}/{Id}", retailer, id);
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = ex.Message });
            return notFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating rule {Retailer}/{Id}", retailer, id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Deactivate (soft delete) a rule.
    /// DELETE /api/rules/{retailer}/{id}
    /// </summary>
    [Function("DeactivateRule")]
    public async Task<HttpResponseData> DeactivateRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rules/{retailer}/{id}")] HttpRequestData req,
        string retailer,
        string id)
    {
        try
        {
            await _ruleService.DeactivateRuleAsync(retailer, id);

            _logger.LogInformation("Rule deactivated: {Retailer}/{Id}", retailer, id);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, message = $"Rule '{id}' deactivated" });
            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Rule not found: {Retailer}/{Id}", retailer, id);
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = ex.Message });
            return notFound;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating rule {Retailer}/{Id}", retailer, id);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    private static object MapRuleToResponse(RetailerRule r)
    {
        return new
        {
            id = r.RowKey,
            retailer = r.PartitionKey,
            rule_type = r.RuleType,
            pattern = r.Pattern,
            action = r.Action,
            target_value = r.TargetValue,
            created_from = r.CreatedFrom,
            created_by = r.CreatedBy,
            created_at = r.CreatedAt,
            hit_count = r.HitCount,
            is_active = r.IsActive,
            description = r.Description
        };
    }
}

// Request DTOs
public class CreateRuleRequest
{
    [JsonPropertyName("retailer")]
    public string Retailer { get; set; } = string.Empty;

    [JsonPropertyName("rule_type")]
    public string RuleType { get; set; } = string.Empty;

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("target_value")]
    public string? TargetValue { get; set; }

    [JsonPropertyName("created_from")]
    public string? CreatedFrom { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class UpdateRuleRequest
{
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("target_value")]
    public string? TargetValue { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
