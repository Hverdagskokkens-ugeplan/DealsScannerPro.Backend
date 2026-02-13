using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface IRetailerRuleService
{
    /// <summary>
    /// Get active rules for a retailer, optionally filtered by rule type.
    /// </summary>
    Task<List<RetailerRule>> GetRulesAsync(string retailer, string? ruleType = null);

    /// <summary>
    /// Get a single rule by retailer and rule ID.
    /// </summary>
    Task<RetailerRule?> GetRuleAsync(string retailer, string ruleId);

    /// <summary>
    /// Create a new retailer rule. Validates rule type and sets keys.
    /// </summary>
    Task<RetailerRule> CreateRuleAsync(RetailerRule rule);

    /// <summary>
    /// Update an existing rule (partial update via read-modify-write).
    /// </summary>
    Task<RetailerRule> UpdateRuleAsync(string retailer, string ruleId, RetailerRule update);

    /// <summary>
    /// Soft delete a rule (sets IsActive = false).
    /// </summary>
    Task DeactivateRuleAsync(string retailer, string ruleId);

    /// <summary>
    /// Increment the hit counter for a rule (called by scanner when rule matches).
    /// </summary>
    Task IncrementHitCountAsync(string retailer, string ruleId);
}
