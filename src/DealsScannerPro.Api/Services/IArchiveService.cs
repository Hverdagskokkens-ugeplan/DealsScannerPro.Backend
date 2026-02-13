using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface IArchiveService
{
    Task<ArchiveRunResult> RunArchiveAsync();
    Task<List<Offer>> GetArchivedOffersAsync(string? retailer = null, string? product = null, DateTime? from = null, DateTime? to = null, int maxResults = 100);
}

public class ArchiveRunResult
{
    public int OffersArchived { get; set; }
    public int TilbudArchived { get; set; }
    public int OffersHardDeleted { get; set; }
    public int TilbudHardDeleted { get; set; }
    public bool Enabled { get; set; }
    public int GraceDays { get; set; }
    public int RetentionDays { get; set; }
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public List<string> Errors { get; set; } = new();
}
