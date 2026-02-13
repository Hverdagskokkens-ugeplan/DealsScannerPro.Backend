using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class SystemSettingsService : ISystemSettingsService
{
    private readonly TableClient _settingsTable;
    private readonly ILogger<SystemSettingsService> _logger;

    // In-memory cache with expiration
    private static Dictionary<string, string>? _cachedSettings;
    private static DateTime _cacheExpiration = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly object _cacheLock = new();

    public SystemSettingsService(IConfiguration configuration, ILogger<SystemSettingsService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _settingsTable = serviceClient.GetTableClient("SystemSettings");
        _settingsTable.CreateIfNotExists();
    }

    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_cachedSettings != null && DateTime.UtcNow < _cacheExpiration)
            {
                return new Dictionary<string, string>(_cachedSettings);
            }
        }

        // Fetch from Table Storage
        var settings = new Dictionary<string, string>();
        await foreach (var entity in _settingsTable.QueryAsync<SystemSetting>(s => s.PartitionKey == "settings"))
        {
            settings[entity.RowKey] = entity.Value;
        }

        // If no settings exist, seed defaults
        if (settings.Count == 0)
        {
            _logger.LogInformation("No settings found, seeding defaults...");
            await SeedDefaultSettingsAsync();
            settings = await FetchSettingsFromStorageAsync();
        }

        // Update cache
        lock (_cacheLock)
        {
            _cachedSettings = new Dictionary<string, string>(settings);
            _cacheExpiration = DateTime.UtcNow.Add(CacheDuration);
        }

        _logger.LogDebug("Loaded {Count} settings from storage", settings.Count);
        return settings;
    }

    private async Task<Dictionary<string, string>> FetchSettingsFromStorageAsync()
    {
        var settings = new Dictionary<string, string>();
        await foreach (var entity in _settingsTable.QueryAsync<SystemSetting>(s => s.PartitionKey == "settings"))
        {
            settings[entity.RowKey] = entity.Value;
        }
        return settings;
    }

    public async Task<string> GetSettingAsync(string key, string defaultValue = "")
    {
        var settings = await GetAllSettingsAsync();
        return settings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public async Task<int> GetSettingIntAsync(string key, int defaultValue = 0)
    {
        var value = await GetSettingAsync(key);
        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    public async Task<bool> GetSettingBoolAsync(string key, bool defaultValue = false)
    {
        var value = await GetSettingAsync(key);
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }

    public async Task<SystemSetting> UpsertSettingAsync(string key, string value, string? description = null, string? dataType = null, string? updatedBy = null)
    {
        var setting = new SystemSetting
        {
            PartitionKey = "settings",
            RowKey = key,
            Value = value,
            Description = description,
            DataType = dataType,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = updatedBy
        };

        // Preserve existing description/dataType if not provided
        try
        {
            var existing = await _settingsTable.GetEntityAsync<SystemSetting>("settings", key);
            if (description == null) setting.Description = existing.Value.Description;
            if (dataType == null) setting.DataType = existing.Value.DataType;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // New setting, no existing values to preserve
        }

        await _settingsTable.UpsertEntityAsync(setting, TableUpdateMode.Replace);
        ClearCache();

        _logger.LogInformation("Upserted setting: {Key} = {Value}", key, value);
        return setting;
    }

    public async Task DeleteSettingAsync(string key)
    {
        try
        {
            await _settingsTable.DeleteEntityAsync("settings", key);
            ClearCache();
            _logger.LogInformation("Deleted setting: {Key}", key);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Setting {Key} not found for deletion", key);
        }
    }

    public async Task SeedDefaultSettingsAsync()
    {
        var defaults = new List<SystemSetting>
        {
            new()
            {
                RowKey = SettingKeys.ArchiveRetentionDays,
                Value = "180",
                Description = "Days to keep archived data before hard delete",
                DataType = "int"
            },
            new()
            {
                RowKey = SettingKeys.ArchiveEnabled,
                Value = "true",
                Description = "Enable/disable automatic archiving",
                DataType = "bool"
            },
            new()
            {
                RowKey = SettingKeys.ArchiveGraceDays,
                Value = "1",
                Description = "Days after ValidTo before archiving",
                DataType = "int"
            }
        };

        foreach (var setting in defaults)
        {
            try
            {
                // Only insert if doesn't exist
                try
                {
                    await _settingsTable.GetEntityAsync<SystemSetting>("settings", setting.RowKey);
                    _logger.LogDebug("Setting {Key} already exists, skipping", setting.RowKey);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    await _settingsTable.AddEntityAsync(setting);
                    _logger.LogInformation("Seeded setting: {Key} = {Value}", setting.RowKey, setting.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed setting: {Key}", setting.RowKey);
            }
        }

        ClearCache();
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedSettings = null;
            _cacheExpiration = DateTime.MinValue;
        }
        _logger.LogDebug("Settings cache cleared");
    }
}
