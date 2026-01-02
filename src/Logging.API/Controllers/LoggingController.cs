using Logging.API.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace Logging.API.Controllers;

[Route("api/logging")]
[ApiController]
public class LoggingController : ControllerBase
{
    private readonly ILogger<LoggingController> _logger;
    private SampleService _sampleService;
    private static readonly ActivitySource ActivitySource = new("SampleController");

    public LoggingController(ILogger<LoggingController> logger, SampleService sampleService)
    {
        _logger = logger;
        _sampleService = sampleService;
    }

    [HttpGet]
    public async Task<IActionResult?> Get()
    {
        using var activity = ActivitySource.StartActivity("GetApi");

        _logger.LogInformation("This is an information log from LoggingController =====> 1 .");

        await _sampleService.GetOrders();

        _logger.LogInformation("This is an information log from LoggingController =====> 2.");

        return Ok();
    }
}
