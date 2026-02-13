namespace DealsScannerPro.Api.Services;

public interface IReprocessService
{
    Task<ReprocessResult> ReprocessAsync(string flyerId, string triggeredBy, string? reason);
}

public class ReprocessResult
{
    public string ProcessingRunId { get; set; } = string.Empty;
    public string FlyerId { get; set; } = string.Empty;
    public string Retailer { get; set; } = string.Empty;
    public int TotalOffers { get; set; }
    public int RulesApplied { get; set; }
    public int OffersModified { get; set; }
    public int OffersSkipped { get; set; }
    public int Errors { get; set; }
    public MetricSnapshot Before { get; set; } = new();
    public MetricSnapshot After { get; set; } = new();
    public ImprovementMetrics Improvement { get; set; } = new();
    public List<RuleApplicationDetail> RulesAppliedDetails { get; set; } = new();
}

public class MetricSnapshot
{
    public int HighConfidence { get; set; }
    public int LowConfidence { get; set; }
    public double AvgConfidence { get; set; }
    public int Published { get; set; }
    public int NeedsReview { get; set; }
    public int Deleted { get; set; }
}

public class ImprovementMetrics
{
    public int HighConfidenceDelta { get; set; }
    public double AvgConfidenceDelta { get; set; }
}

public class RuleApplicationDetail
{
    public string RuleId { get; set; } = string.Empty;
    public string RuleType { get; set; } = string.Empty;
    public int HitCount { get; set; }
}
