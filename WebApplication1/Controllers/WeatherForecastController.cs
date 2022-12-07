using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace WebApplication1.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    private readonly ILogger<WeatherForecastController> _logger;

    public WeatherForecastController(ILogger<WeatherForecastController> logger)
    {
        _logger = logger;
    }

    [HttpGet(Name = "Shell")]
    public IActionResult Get()
    {
        var stdErrStringBuilder = new StringBuilder();
        var stdOutStringBuilder = new StringBuilder();
        int exitCode;
        
        exitCode = SilentProcessRunner.ExecuteCommand(
            "Binaries/kubectl",
            "version --output=yaml --client",
            Environment.CurrentDirectory,
            s => { },
            x => stdOutStringBuilder.AppendLine(x),
            s => stdErrStringBuilder.AppendLine(s)
            );
        return Ok(stdOutStringBuilder.ToString());
    }
}