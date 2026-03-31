using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DmarcAnalyzer.Api.Workers;

public sealed class QueueWorkerService(ILogger<QueueWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Queue worker heartbeat at {TimestampUtc}", DateTime.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }

        logger.LogInformation("Queue worker stopping.");
    }
}
