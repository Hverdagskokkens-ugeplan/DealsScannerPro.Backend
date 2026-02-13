using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface ISystemSettingsService
{
    Task<Dictionary<string, string>> GetAllSettingsAsync();
    Task<string> GetSettingAsync(string key, string defaultValue = "");
    Task<int> GetSettingIntAsync(string key, int defaultValue = 0);
    Task<bool> GetSettingBoolAsync(string key, bool defaultValue = false);
    Task<SystemSetting> UpsertSettingAsync(string key, string value, string? description = null, string? dataType = null, string? updatedBy = null);
    Task DeleteSettingAsync(string key);
    Task SeedDefaultSettingsAsync();
    void ClearCache();
}
