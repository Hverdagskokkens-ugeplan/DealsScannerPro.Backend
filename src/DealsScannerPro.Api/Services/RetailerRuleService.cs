using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class RetailerRuleService : IRetailerRuleService
{
    private readonly TableClient _rulesTable;
    private readonly ILogger<RetailerRuleService> _logger;

    public RetailerRuleService(IConfiguration configuration, ILogger<RetailerRuleService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _rulesTable = serviceClient.GetTableClient("RetailerRules");
        _rulesTable.CreateIfNotExists();
    }

    public async Task<List<RetailerRule>> GetRulesAsync(string retailer, string? ruleType = null)
    {
        var partitionKey = retailer.ToLowerInvariant();
        var rules = new List<RetailerRule>();

        await foreach (var entity in _rulesTable.QueryAsync<RetailerRule>(
            r => r.PartitionKey == partitionKey && r.IsActive))
        {
            if (ruleType == null || entity.RuleType == ruleType)
            {
                rules.Add(entity);
            }
        }

        _logger.LogDebug("Loaded {Count} rules for {Retailer} (type={RuleType})",
            rules.Count, retailer, ruleType ?? "all");

        return rules;
    }

    public async Task<RetailerRule?> GetRuleAsync(string retailer, string ruleId)
    {
        var partitionKey = retailer.ToLowerInvariant();

        try
        {
            var response = await _rulesTable.GetEntityAsync<RetailerRule>(partitionKey, ruleId);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<RetailerRule> CreateRuleAsync(RetailerRule rule)
    {
        // Validate rule type
        if (!RuleTypes.All.Contains(rule.RuleType))
        {
            throw new ArgumentException(
                $"Invalid rule type '{rule.RuleType}'. Must be one of: {string.Join(", ", RuleTypes.All)}");
        }

        // Set keys
        rule.PartitionKey = rule.PartitionKey.ToLowerInvariant();
        rule.RowKey = $"{rule.RuleType}_{Guid.NewGuid():N}";
        rule.CreatedAt = DateTime.UtcNow;
        rule.IsActive = true;
        rule.HitCount = 0;

        await _rulesTable.AddEntityAsync(rule);

        _logger.LogInformation("Created rule {RowKey} for {Retailer}: {RuleType} pattern='{Pattern}'",
            rule.RowKey, rule.PartitionKey, rule.RuleType, rule.Pattern);

        return rule;
    }

    public async Task<RetailerRule> UpdateRuleAsync(string retailer, string ruleId, RetailerRule update)
    {
        var partitionKey = retailer.ToLowerInvariant();

        // Read existing
        var existing = await GetRuleAsync(retailer, ruleId)
            ?? throw new InvalidOperationException($"Rule '{ruleId}' not found for retailer '{retailer}'");

        // Apply partial updates
        if (!string.IsNullOrEmpty(update.Pattern))
            existing.Pattern = update.Pattern;
        if (!string.IsNullOrEmpty(update.Action))
            existing.Action = update.Action;
        if (update.TargetValue != null)
            existing.TargetValue = update.TargetValue;
        if (update.Description != null)
            existing.Description = update.Description;

        await _rulesTable.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace);

        _logger.LogInformation("Updated rule {RowKey} for {Retailer}", ruleId, retailer);

        return existing;
    }

    public async Task DeactivateRuleAsync(string retailer, string ruleId)
    {
        var existing = await GetRuleAsync(retailer, ruleId)
            ?? throw new InvalidOperationException($"Rule '{ruleId}' not found for retailer '{retailer}'");

        existing.IsActive = false;

        await _rulesTable.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace);

        _logger.LogInformation("Deactivated rule {RowKey} for {Retailer}", ruleId, retailer);
    }

    public async Task IncrementHitCountAsync(string retailer, string ruleId)
    {
        var existing = await GetRuleAsync(retailer, ruleId);
        if (existing == null) return;

        existing.HitCount++;

        await _rulesTable.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace);
    }
}
