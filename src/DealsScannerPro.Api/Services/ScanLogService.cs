using System.Text.Json;
using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public interface IScanLogService
{
    Task<ScanLog> LogScanAsync(LogScanRequest request);
    Task<List<ScanLogResponse>> GetScansAsync(int limit = 20);
}

public class ScanLogService : IScanLogService
{
    private readonly TableClient _scanLogsTable;
    private readonly ILogger<ScanLogService> _logger;

    public ScanLogService(IConfiguration configuration, ILogger<ScanLogService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _scanLogsTable = serviceClient.GetTableClient("ScanLogs");
        _scanLogsTable.CreateIfNotExists();
    }

    public async Task<ScanLog> LogScanAsync(LogScanRequest request)
    {
        var now = DateTime.UtcNow;
        var scanId = request.ScanId ?? Guid.NewGuid().ToString();

        var scanLog = new ScanLog
        {
            PartitionKey = "scanlogs",
            RowKey = ScanLog.GenerateRowKey(now),
            ScanId = scanId,
            ScanTimestamp = now,
            SourceFile = request.SourceFile,
            Retailer = request.Retailer,
            ValidFrom = request.ValidFrom,
            ValidTo = request.ValidTo,
            ServicesUsedJson = request.ServicesUsed != null
                ? JsonSerializer.Serialize(request.ServicesUsed)
                : null,
            PagesScanned = request.PagesScanned,
            OffersDetected = request.OffersDetected,
            OffersExtracted = request.OffersExtracted,
            OffersWithCandidates = request.OffersWithCandidates,
            AvgConfidence = request.AvgConfidence,
            OffersUploaded = request.OffersUploaded,
            Status = request.Status,
            ErrorMessage = request.ErrorMessage,
            WarningsJson = request.Warnings?.Count > 0
                ? JsonSerializer.Serialize(request.Warnings)
                : null
        };

        await _scanLogsTable.UpsertEntityAsync(scanLog);

        _logger.LogInformation(
            "Logged scan {ScanId}: {Retailer}, {OffersExtracted} offers, status={Status}",
            scanId, request.Retailer, request.OffersExtracted, request.Status);

        return scanLog;
    }

    public async Task<List<ScanLogResponse>> GetScansAsync(int limit = 20)
    {
        var scans = new List<ScanLogResponse>();

        // Query with limit - RowKey is inverted timestamp so this returns newest first
        var query = _scanLogsTable.QueryAsync<ScanLog>(
            filter: $"PartitionKey eq 'scanlogs'",
            maxPerPage: limit);

        await foreach (var page in query.AsPages())
        {
            foreach (var scan in page.Values)
            {
                scans.Add(MapToResponse(scan));
                if (scans.Count >= limit) break;
            }
            if (scans.Count >= limit) break;
        }

        return scans;
    }

    private static ScanLogResponse MapToResponse(ScanLog scan)
    {
        ScanServicesUsed? services = null;
        if (!string.IsNullOrEmpty(scan.ServicesUsedJson))
        {
            try
            {
                services = JsonSerializer.Deserialize<ScanServicesUsed>(scan.ServicesUsedJson);
            }
            catch { }
        }

        List<string>? warnings = null;
        if (!string.IsNullOrEmpty(scan.WarningsJson))
        {
            try
            {
                warnings = JsonSerializer.Deserialize<List<string>>(scan.WarningsJson);
            }
            catch { }
        }

        return new ScanLogResponse
        {
            ScanId = scan.ScanId,
            Timestamp = scan.ScanTimestamp,
            SourceFile = scan.SourceFile,
            Retailer = scan.Retailer,
            ValidFrom = scan.ValidFrom,
            ValidTo = scan.ValidTo,
            ServicesUsed = services,
            Results = new ScanResultsResponse
            {
                PagesScanned = scan.PagesScanned,
                OffersDetected = scan.OffersDetected,
                OffersExtracted = scan.OffersExtracted,
                OffersWithCandidates = scan.OffersWithCandidates,
                AvgConfidence = scan.AvgConfidence,
                OffersUploaded = scan.OffersUploaded
            },
            Status = scan.Status,
            ErrorMessage = scan.ErrorMessage,
            Warnings = warnings
        };
    }
}

/// <summary>
/// Response model for scan log entries.
/// </summary>
public class ScanLogResponse
{
    public string ScanId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
    public ScanServicesUsed? ServicesUsed { get; set; }
    public ScanResultsResponse Results { get; set; } = new();
    public string Status { get; set; } = "completed";
    public string? ErrorMessage { get; set; }
    public List<string>? Warnings { get; set; }
}

public class ScanResultsResponse
{
    public int PagesScanned { get; set; }
    public int OffersDetected { get; set; }
    public int OffersExtracted { get; set; }
    public int OffersWithCandidates { get; set; }
    public double AvgConfidence { get; set; }
    public int OffersUploaded { get; set; }
}
