using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class ArchiveService : IArchiveService
{
    private readonly TableClient _offersTable;
    private readonly TableClient _tilbudTable;
    private readonly TableClient _archivedTilbudTable;
    private readonly ISystemSettingsService _settingsService;
    private readonly ILogger<ArchiveService> _logger;

    public ArchiveService(IConfiguration configuration, ISystemSettingsService settingsService, ILogger<ArchiveService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _offersTable = serviceClient.GetTableClient("Offers");
        _tilbudTable = serviceClient.GetTableClient("Tilbud");
        _archivedTilbudTable = serviceClient.GetTableClient("ArchivedTilbud");

        _offersTable.CreateIfNotExists();
        _tilbudTable.CreateIfNotExists();
        _archivedTilbudTable.CreateIfNotExists();
    }

    public async Task<ArchiveRunResult> RunArchiveAsync()
    {
        var result = new ArchiveRunResult { RunAt = DateTime.UtcNow };

        // Read settings
        result.Enabled = await _settingsService.GetSettingBoolAsync(SettingKeys.ArchiveEnabled, true);
        result.GraceDays = await _settingsService.GetSettingIntAsync(SettingKeys.ArchiveGraceDays, 1);
        result.RetentionDays = await _settingsService.GetSettingIntAsync(SettingKeys.ArchiveRetentionDays, 180);

        if (!result.Enabled)
        {
            _logger.LogInformation("Archive is disabled, skipping run");
            return result;
        }

        var today = DateTime.UtcNow.Date;
        var archiveCutoff = today.AddDays(-result.GraceDays);
        var purgeCutoff = today.AddDays(-result.RetentionDays);

        _logger.LogInformation(
            "Running archive: graceDays={GraceDays}, retentionDays={RetentionDays}, archiveCutoff={ArchiveCutoff}, purgeCutoff={PurgeCutoff}",
            result.GraceDays, result.RetentionDays, archiveCutoff, purgeCutoff);

        // 1. Archive expired Offers (set status to "archived")
        result.OffersArchived = await ArchiveExpiredOffersAsync(archiveCutoff, result.Errors);

        // 2. Archive expired Tilbud (copy to ArchivedTilbud, delete from Tilbud)
        result.TilbudArchived = await ArchiveExpiredTilbudAsync(archiveCutoff, result.Errors);

        // 3. Purge old archived Offers (hard delete)
        result.OffersHardDeleted = await PurgeOldOffersAsync(purgeCutoff, result.Errors);

        // 4. Purge old ArchivedTilbud (hard delete)
        result.TilbudHardDeleted = await PurgeOldArchivedTilbudAsync(purgeCutoff, result.Errors);

        _logger.LogInformation(
            "Archive complete: offersArchived={OffersArchived}, tilbudArchived={TilbudArchived}, offersDeleted={OffersDeleted}, tilbudDeleted={TilbudDeleted}, errors={ErrorCount}",
            result.OffersArchived, result.TilbudArchived, result.OffersHardDeleted, result.TilbudHardDeleted, result.Errors.Count);

        return result;
    }

    private async Task<int> ArchiveExpiredOffersAsync(DateTime archiveCutoff, List<string> errors)
    {
        var count = 0;
        var cutoffStr = archiveCutoff.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"ValidTo lt datetime'{cutoffStr}' and Status ne '{OfferStatus.Archived}' and Status ne '{OfferStatus.Deleted}'";

        await foreach (var offer in _offersTable.QueryAsync<Offer>(filter, maxPerPage: 100))
        {
            try
            {
                offer.Status = OfferStatus.Archived;
                await _offersTable.UpsertEntityAsync(offer, TableUpdateMode.Replace);
                count++;
            }
            catch (Exception ex)
            {
                var msg = $"Failed to archive offer {offer.PartitionKey}|{offer.RowKey}: {ex.Message}";
                _logger.LogWarning(ex, "Failed to archive offer {PK}|{RK}", offer.PartitionKey, offer.RowKey);
                errors.Add(msg);
            }
        }

        if (count > 0)
            _logger.LogInformation("Archived {Count} expired offers", count);

        return count;
    }

    private async Task<int> ArchiveExpiredTilbudAsync(DateTime archiveCutoff, List<string> errors)
    {
        var count = 0;
        var cutoffStr = archiveCutoff.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"GyldigTil lt datetime'{cutoffStr}'";

        await foreach (var tilbud in _tilbudTable.QueryAsync<Tilbud>(filter, maxPerPage: 100))
        {
            try
            {
                // Copy to ArchivedTilbud
                var archived = new Tilbud
                {
                    PartitionKey = tilbud.PartitionKey,
                    RowKey = tilbud.RowKey,
                    Produkt = tilbud.Produkt,
                    TotalPris = tilbud.TotalPris,
                    PrisPerEnhed = tilbud.PrisPerEnhed,
                    Enhed = tilbud.Enhed,
                    Maengde = tilbud.Maengde,
                    MaengdeValue = tilbud.MaengdeValue,
                    MaengdeUnit = tilbud.MaengdeUnit,
                    Kategori = tilbud.Kategori,
                    Konfidens = tilbud.Konfidens,
                    Side = tilbud.Side,
                    KildeFil = tilbud.KildeFil,
                    Varianter = tilbud.Varianter,
                    Kommentar = tilbud.Kommentar,
                    Butik = tilbud.Butik,
                    GyldigFra = tilbud.GyldigFra,
                    GyldigTil = tilbud.GyldigTil,
                    Oprettet = tilbud.Oprettet
                };

                await _archivedTilbudTable.UpsertEntityAsync(archived, TableUpdateMode.Replace);

                // Delete from Tilbud
                await _tilbudTable.DeleteEntityAsync(tilbud.PartitionKey, tilbud.RowKey);
                count++;
            }
            catch (Exception ex)
            {
                var msg = $"Failed to archive tilbud {tilbud.PartitionKey}|{tilbud.RowKey}: {ex.Message}";
                _logger.LogWarning(ex, "Failed to archive tilbud {PK}|{RK}", tilbud.PartitionKey, tilbud.RowKey);
                errors.Add(msg);
            }
        }

        if (count > 0)
            _logger.LogInformation("Archived {Count} expired tilbud", count);

        return count;
    }

    private async Task<int> PurgeOldOffersAsync(DateTime purgeCutoff, List<string> errors)
    {
        var count = 0;
        var cutoffStr = purgeCutoff.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"Status eq '{OfferStatus.Archived}' and ValidTo lt datetime'{cutoffStr}'";

        await foreach (var offer in _offersTable.QueryAsync<Offer>(filter, maxPerPage: 100))
        {
            try
            {
                await _offersTable.DeleteEntityAsync(offer.PartitionKey, offer.RowKey);
                count++;
            }
            catch (Exception ex)
            {
                var msg = $"Failed to purge offer {offer.PartitionKey}|{offer.RowKey}: {ex.Message}";
                _logger.LogWarning(ex, "Failed to purge offer {PK}|{RK}", offer.PartitionKey, offer.RowKey);
                errors.Add(msg);
            }
        }

        if (count > 0)
            _logger.LogInformation("Purged {Count} old archived offers", count);

        return count;
    }

    private async Task<int> PurgeOldArchivedTilbudAsync(DateTime purgeCutoff, List<string> errors)
    {
        var count = 0;
        var cutoffStr = purgeCutoff.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"GyldigTil lt datetime'{cutoffStr}'";

        await foreach (var tilbud in _archivedTilbudTable.QueryAsync<Tilbud>(filter, maxPerPage: 100))
        {
            try
            {
                await _archivedTilbudTable.DeleteEntityAsync(tilbud.PartitionKey, tilbud.RowKey);
                count++;
            }
            catch (Exception ex)
            {
                var msg = $"Failed to purge archived tilbud {tilbud.PartitionKey}|{tilbud.RowKey}: {ex.Message}";
                _logger.LogWarning(ex, "Failed to purge archived tilbud {PK}|{RK}", tilbud.PartitionKey, tilbud.RowKey);
                errors.Add(msg);
            }
        }

        if (count > 0)
            _logger.LogInformation("Purged {Count} old archived tilbud", count);

        return count;
    }

    public async Task<List<Offer>> GetArchivedOffersAsync(string? retailer = null, string? product = null, DateTime? from = null, DateTime? to = null, int maxResults = 100)
    {
        var results = new List<Offer>();

        // Query archived Offers
        var filters = new List<string> { $"Status eq '{OfferStatus.Archived}'" };

        if (!string.IsNullOrEmpty(retailer))
            filters.Add($"Retailer eq '{retailer.ToLowerInvariant()}'");

        if (from.HasValue)
        {
            var fromStr = from.Value.ToString("yyyy-MM-ddTHH:mm:ssZ");
            filters.Add($"ValidFrom ge datetime'{fromStr}'");
        }

        if (to.HasValue)
        {
            var toStr = to.Value.ToString("yyyy-MM-ddTHH:mm:ssZ");
            filters.Add($"ValidTo le datetime'{toStr}'");
        }

        var filter = string.Join(" and ", filters);

        await foreach (var offer in _offersTable.QueryAsync<Offer>(filter, maxPerPage: maxResults))
        {
            if (!string.IsNullOrEmpty(product) &&
                !(offer.ProductNorm?.Contains(product, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(offer.ProductTextRaw?.Contains(product, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            results.Add(offer);
            if (results.Count >= maxResults) break;
        }

        return results;
    }
}
