using System.Text.Json;
using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class TableStorageService : ITableStorageService
{
    private readonly TableClient _tilbudTable;
    private readonly TableClient _butikkerTable;
    private readonly ILogger<TableStorageService> _logger;

    public TableStorageService(IConfiguration configuration, ILogger<TableStorageService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);

        _tilbudTable = serviceClient.GetTableClient("Tilbud");
        _butikkerTable = serviceClient.GetTableClient("Butikker");

        // Ensure tables exist
        _tilbudTable.CreateIfNotExists();
        _butikkerTable.CreateIfNotExists();
    }

    public async Task<int> UploadTilbudAsync(UploadRequest request)
    {
        var butik = request.Meta.Butik.ToLowerInvariant();
        var gyldigFra = DateTime.SpecifyKind(
            DateTime.Parse(request.Meta.GyldigFra ?? DateTime.UtcNow.ToString("yyyy-MM-dd")),
            DateTimeKind.Utc);
        var gyldigTil = DateTime.SpecifyKind(
            DateTime.Parse(request.Meta.GyldigTil ?? DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd")),
            DateTimeKind.Utc);

        var partitionKey = $"{butik}_{gyldigFra:yyyy-MM-dd}_{gyldigTil:yyyy-MM-dd}";

        var count = 0;
        foreach (var t in request.Tilbud)
        {
            var entity = new Tilbud
            {
                PartitionKey = partitionKey,
                RowKey = Guid.NewGuid().ToString(),
                Produkt = t.Produkt,
                TotalPris = t.TotalPris,
                PrisPerEnhed = t.PrisPerEnhed,
                Enhed = t.Enhed,
                Maengde = t.Maengde,
                MaengdeValue = t.MaengdeNormaliseret?.Value,
                MaengdeUnit = t.MaengdeNormaliseret?.Unit,
                Kategori = t.Kategori,
                Konfidens = t.Konfidens,
                Side = t.Side,
                KildeFil = request.Meta.KildeFil,
                Varianter = t.Varianter != null ? JsonSerializer.Serialize(t.Varianter) : null,
                Kommentar = t.Kommentar,
                Butik = butik,
                GyldigFra = gyldigFra,
                GyldigTil = gyldigTil,
                Oprettet = DateTime.UtcNow
            };

            await _tilbudTable.AddEntityAsync(entity);
            count++;
        }

        _logger.LogInformation("Uploaded {Count} tilbud for {Butik} ({From} - {To})",
            count, butik, gyldigFra, gyldigTil);

        return count;
    }

    public async Task<List<Tilbud>> GetTilbudAsync(string? butik = null, string? kategori = null, int? maxResults = 100)
    {
        var results = new List<Tilbud>();
        var filter = "";

        if (!string.IsNullOrEmpty(butik))
        {
            filter = $"Butik eq '{butik.ToLowerInvariant()}'";
        }

        if (!string.IsNullOrEmpty(kategori))
        {
            var kategoriFilter = $"Kategori eq '{kategori}'";
            filter = string.IsNullOrEmpty(filter) ? kategoriFilter : $"{filter} and {kategoriFilter}";
        }

        var query = string.IsNullOrEmpty(filter)
            ? _tilbudTable.QueryAsync<Tilbud>(maxPerPage: maxResults)
            : _tilbudTable.QueryAsync<Tilbud>(filter, maxPerPage: maxResults);

        await foreach (var entity in query)
        {
            results.Add(entity);
            if (results.Count >= maxResults) break;
        }

        return results;
    }

    public async Task<Tilbud?> GetTilbudByIdAsync(string partitionKey, string rowKey)
    {
        try
        {
            var response = await _tilbudTable.GetEntityAsync<Tilbud>(partitionKey, rowKey);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<List<Butik>> GetButikkerAsync()
    {
        var results = new List<Butik>();

        await foreach (var entity in _butikkerTable.QueryAsync<Butik>(b => b.PartitionKey == "butikker"))
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task SeedButikkerAsync()
    {
        var butikker = new List<Butik>
        {
            new() { RowKey = "netto", Navn = "Netto", PrimaerFarve = "#FFD700", SekundaerFarve = "#000000" },
            new() { RowKey = "rema", Navn = "Rema 1000", PrimaerFarve = "#E31837", SekundaerFarve = "#FFFFFF" },
            new() { RowKey = "foetex", Navn = "Føtex", PrimaerFarve = "#003DA5", SekundaerFarve = "#FFFFFF" },
            new() { RowKey = "bilka", Navn = "Bilka", PrimaerFarve = "#0066B3", SekundaerFarve = "#FFFFFF" },
            new() { RowKey = "superbrugsen", Navn = "SuperBrugsen", PrimaerFarve = "#E4002B", SekundaerFarve = "#FFFFFF" },
            new() { RowKey = "spar", Navn = "SPAR", PrimaerFarve = "#00843D", SekundaerFarve = "#FFFFFF" },
            new() { RowKey = "365discount", Navn = "365discount", PrimaerFarve = "#FF6600", SekundaerFarve = "#FFFFFF" }
        };

        foreach (var butik in butikker)
        {
            try
            {
                await _butikkerTable.UpsertEntityAsync(butik);
                _logger.LogInformation("Seeded butik: {Butik}", butik.Navn);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed butik: {Butik}", butik.Navn);
            }
        }
    }
}
