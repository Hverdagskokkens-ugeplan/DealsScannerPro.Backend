using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using DealsScannerPro.Api.Services;

namespace DealsScannerPro.Api.Functions;

public class ArchiveTimerFunction
{
    private readonly IArchiveService _archiveService;
    private readonly ILogger<ArchiveTimerFunction> _logger;

    public ArchiveTimerFunction(IArchiveService archiveService, ILogger<ArchiveTimerFunction> logger)
    {
        _archiveService = archiveService;
        _logger = logger;
    }

    /// <summary>
    /// Daily archive timer - runs at 02:00 UTC (03:00 CET).
    /// Archives expired offers and purges old data based on system settings.
    /// </summary>
    [Function("ArchiveTimer")]
    public async Task Run([TimerTrigger("0 0 2 * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("ArchiveTimer triggered at {Time}", DateTime.UtcNow);

        try
        {
            var result = await _archiveService.RunArchiveAsync();

            _logger.LogInformation(
                "ArchiveTimer complete: enabled={Enabled}, offersArchived={OffersArchived}, tilbudArchived={TilbudArchived}, " +
                "offersDeleted={OffersDeleted}, tilbudDeleted={TilbudDeleted}, errors={ErrorCount}",
                result.Enabled, result.OffersArchived, result.TilbudArchived,
                result.OffersHardDeleted, result.TilbudHardDeleted, result.Errors.Count);

            if (result.Errors.Count > 0)
            {
                _logger.LogWarning("ArchiveTimer completed with {ErrorCount} errors: {Errors}",
                    result.Errors.Count, string.Join("; ", result.Errors));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArchiveTimer failed");
            throw;
        }
    }
}
