using System.Diagnostics;

namespace Logging.API.Service;

public class SampleService
{
    private static readonly ActivitySource ActivitySource = new("SampleService");
    private readonly ILogger<SampleService> _logger;

    public SampleService(ILogger<SampleService> logger)
    {
        _logger = logger;
    }

    public async Task GetOrders()
    {
        using var activity = ActivitySource.StartActivity("GetOrders");

        _logger.LogInformation("This is an information log from SampleService =====> 1 .");
        if (new Random().Next(1, 100) % 2 == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        _logger.LogInformation("This is an information log from SampleService =====> 2 .");
    }
}
