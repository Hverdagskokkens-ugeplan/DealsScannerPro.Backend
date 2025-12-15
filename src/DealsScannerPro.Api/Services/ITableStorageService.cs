using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface ITableStorageService
{
    Task<int> UploadTilbudAsync(UploadRequest request);
    Task<List<Tilbud>> GetTilbudAsync(string? butik = null, string? kategori = null, int? maxResults = 100);
    Task<Tilbud?> GetTilbudByIdAsync(string partitionKey, string rowKey);
    Task<List<Butik>> GetButikkerAsync();
    Task SeedButikkerAsync();
    Task<List<Tilbud>> GetTilbudByDateAsync(DateTime dato);
}
