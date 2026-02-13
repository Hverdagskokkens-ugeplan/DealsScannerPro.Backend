using DealsScannerPro.Api.Models;

namespace DealsScannerPro.Api.Services;

public interface IProcessingRunService
{
    Task<ProcessingRun> CreateRunAsync(CreateRunRequest request);
    Task<ProcessingRun> CompleteRunAsync(string flyerId, string runId, CompleteRunRequest request);
    Task<ProcessingRun> FailRunAsync(string flyerId, string runId, string error);
    Task<List<ProcessingRun>> GetRunsAsync(string flyerId);
    Task<ProcessingRun?> GetLatestRunAsync(string flyerId);
    Task<RunComparison> CompareRunsAsync(string flyerId, string runId1, string runId2);
}

public class CreateRunRequest
{
    public string FlyerId { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string TriggeredBy { get; set; } = string.Empty;
}

public class CompleteRunRequest
{
    public RunMetrics Metrics { get; set; } = new();
    public int OffersCreated { get; set; }
    public int OffersUpdated { get; set; }
}

public class RunComparison
{
    public RunSummary Run1 { get; set; } = new();
    public RunSummary Run2 { get; set; } = new();
    public DeltaMetrics Delta { get; set; } = new();
}

public class RunSummary
{
    public string RunId { get; set; } = string.Empty;
    public int RunNumber { get; set; }
    public string TriggerType { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public RunMetrics? Metrics { get; set; }
}

public class DeltaMetrics
{
    public int TotalOffersDelta { get; set; }
    public int HighConfidenceDelta { get; set; }
    public int MediumConfidenceDelta { get; set; }
    public int LowConfidenceDelta { get; set; }
    public double AvgConfidenceDelta { get; set; }
}
