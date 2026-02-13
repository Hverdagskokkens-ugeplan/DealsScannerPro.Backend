using Azure.Data.Tables;
using DealsScannerPro.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DealsScannerPro.Api.Services;

public class ProcessingRunService : IProcessingRunService
{
    private readonly TableClient _runsTable;
    private readonly ILogger<ProcessingRunService> _logger;

    public ProcessingRunService(IConfiguration configuration, ILogger<ProcessingRunService> logger)
    {
        _logger = logger;

        var connectionString = configuration["TableStorageConnection"]
            ?? throw new InvalidOperationException("TableStorageConnection not configured");

        var serviceClient = new TableServiceClient(connectionString);
        _runsTable = serviceClient.GetTableClient("ProcessingRuns");
        _runsTable.CreateIfNotExists();
    }

    public async Task<ProcessingRun> CreateRunAsync(CreateRunRequest request)
    {
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString("N");

        // Count existing runs to determine RunNumber
        var existingCount = 0;
        await foreach (var _ in _runsTable.QueryAsync<ProcessingRun>(
            r => r.PartitionKey == request.FlyerId))
        {
            existingCount++;
        }

        var run = new ProcessingRun
        {
            PartitionKey = request.FlyerId,
            RowKey = ProcessingRun.GenerateRowKey(now),
            RunId = runId,
            FlyerId = request.FlyerId,
            Retailer = request.Retailer,
            RunNumber = existingCount + 1,
            StartedAt = now,
            Status = "running",
            TriggerType = request.TriggerType,
            TriggeredBy = request.TriggeredBy
        };

        await _runsTable.AddEntityAsync(run);

        _logger.LogInformation(
            "Processing run created: RunId={RunId}, FlyerId={FlyerId}, RunNumber={RunNumber}, Trigger={TriggerType}",
            runId, request.FlyerId, run.RunNumber, request.TriggerType);

        return run;
    }

    public async Task<ProcessingRun> CompleteRunAsync(string flyerId, string runId, CompleteRunRequest request)
    {
        var run = await FindRunByIdAsync(flyerId, runId)
            ?? throw new InvalidOperationException($"Processing run '{runId}' not found for flyer '{flyerId}'");

        run.Status = "completed";
        run.CompletedAt = DateTime.UtcNow;
        run.SetMetrics(request.Metrics);
        run.OffersCreated = request.OffersCreated;
        run.OffersUpdated = request.OffersUpdated;

        await _runsTable.UpdateEntityAsync(run, run.ETag, TableUpdateMode.Replace);

        _logger.LogInformation(
            "Processing run completed: RunId={RunId}, FlyerId={FlyerId}, Created={Created}, Updated={Updated}",
            runId, flyerId, request.OffersCreated, request.OffersUpdated);

        return run;
    }

    public async Task<ProcessingRun> FailRunAsync(string flyerId, string runId, string error)
    {
        var run = await FindRunByIdAsync(flyerId, runId)
            ?? throw new InvalidOperationException($"Processing run '{runId}' not found for flyer '{flyerId}'");

        run.Status = "failed";
        run.CompletedAt = DateTime.UtcNow;
        run.SetErrors(new List<string> { error });

        await _runsTable.UpdateEntityAsync(run, run.ETag, TableUpdateMode.Replace);

        _logger.LogWarning(
            "Processing run failed: RunId={RunId}, FlyerId={FlyerId}, Error={Error}",
            runId, flyerId, error);

        return run;
    }

    public async Task<List<ProcessingRun>> GetRunsAsync(string flyerId)
    {
        var runs = new List<ProcessingRun>();

        await foreach (var run in _runsTable.QueryAsync<ProcessingRun>(
            r => r.PartitionKey == flyerId))
        {
            runs.Add(run);
        }

        return runs; // Already sorted newest-first via inverted RowKey
    }

    public async Task<ProcessingRun?> GetLatestRunAsync(string flyerId)
    {
        await foreach (var run in _runsTable.QueryAsync<ProcessingRun>(
            r => r.PartitionKey == flyerId, maxPerPage: 1))
        {
            return run; // First result is newest due to inverted RowKey
        }

        return null;
    }

    public async Task<RunComparison> CompareRunsAsync(string flyerId, string runId1, string runId2)
    {
        var run1 = await FindRunByIdAsync(flyerId, runId1)
            ?? throw new InvalidOperationException($"Processing run '{runId1}' not found for flyer '{flyerId}'");
        var run2 = await FindRunByIdAsync(flyerId, runId2)
            ?? throw new InvalidOperationException($"Processing run '{runId2}' not found for flyer '{flyerId}'");

        var metrics1 = run1.GetMetrics() ?? new RunMetrics();
        var metrics2 = run2.GetMetrics() ?? new RunMetrics();

        return new RunComparison
        {
            Run1 = new RunSummary
            {
                RunId = run1.RunId,
                RunNumber = run1.RunNumber,
                TriggerType = run1.TriggerType,
                StartedAt = run1.StartedAt,
                CompletedAt = run1.CompletedAt,
                Status = run1.Status,
                Metrics = metrics1
            },
            Run2 = new RunSummary
            {
                RunId = run2.RunId,
                RunNumber = run2.RunNumber,
                TriggerType = run2.TriggerType,
                StartedAt = run2.StartedAt,
                CompletedAt = run2.CompletedAt,
                Status = run2.Status,
                Metrics = metrics2
            },
            Delta = new DeltaMetrics
            {
                TotalOffersDelta = metrics2.TotalOffers - metrics1.TotalOffers,
                HighConfidenceDelta = metrics2.HighConfidence - metrics1.HighConfidence,
                MediumConfidenceDelta = metrics2.MediumConfidence - metrics1.MediumConfidence,
                LowConfidenceDelta = metrics2.LowConfidence - metrics1.LowConfidence,
                AvgConfidenceDelta = Math.Round(metrics2.AvgConfidence - metrics1.AvgConfidence, 4)
            }
        };
    }

    private async Task<ProcessingRun?> FindRunByIdAsync(string flyerId, string runId)
    {
        await foreach (var run in _runsTable.QueryAsync<ProcessingRun>(
            r => r.PartitionKey == flyerId && r.RunId == runId))
        {
            return run;
        }

        return null;
    }
}
