using InitiativeMerger.Core.Models;
using InitiativeMerger.Core.Services;
using InitiativeMerger.Web;
using Microsoft.AspNetCore.Mvc;

namespace InitiativeMerger.Web.Controllers;

/// <summary>
/// REST API controller for programmatic access to the merge functionality.
/// Useful for CI/CD pipelines or integration with other tools.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class InitiativeController : ControllerBase
{
    private readonly IInitiativeMergerService _mergerService;
    private readonly IAzurePolicyService _azurePolicyService;
    private readonly ILogger<InitiativeController> _logger;

    public InitiativeController(
        IInitiativeMergerService mergerService,
        IAzurePolicyService azurePolicyService,
        ILogger<InitiativeController> logger)
    {
        _mergerService = mergerService;
        _azurePolicyService = azurePolicyService;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves the list of supported well-known initiatives.
    /// </summary>
    [HttpGet("known")]
    [ProducesResponseType<IEnumerable<object>>(StatusCodes.Status200OK)]
    public IActionResult GetKnownInitiatives()
    {
        var result = WellKnownInitiative.All.Select(i => new
        {
            key = i.Key,
            displayName = i.DisplayName,
            description = i.Description,
            version = i.Version,
            resourceId = i.ResourceId
        });

        return Ok(result);
    }

    /// <summary>
    /// Executes a merge operation and returns the result as JSON.
    /// </summary>
    /// <param name="request">The merge configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("merge")]
    [ProducesResponseType<MergeResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Merge(
        [FromBody] MergeRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        _logger.LogInformation("API merge request received from {RemoteIp}",
            HttpContext.Connection.RemoteIpAddress);

        var result = await _mergerService.MergeAsync(request, cancellationToken);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Checks the Azure CLI status (available and logged in).
    /// </summary>
    [HttpGet("azure-status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAzureStatus(CancellationToken cancellationToken)
    {
        var status = await _azurePolicyService.CheckAzureCliStatusAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>Downloads the current initiative as a JSON file.</summary>
    [HttpGet("download/json")]
    public IActionResult DownloadJson()
    {
        var (json, name) = DownloadCache.Get();
        if (json is null) return NotFound("No initiative available for download.");
        var slug = Slugify(name ?? "initiative");
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"{slug}.json");
    }

    /// <summary>Downloads the current initiative as a Bicep definition file.</summary>
    [HttpGet("download/bicep")]
    public IActionResult DownloadBicep()
    {
        var (json, name) = DownloadCache.Get();
        if (json is null) return NotFound("No initiative available for download.");
        var bicep = _mergerService.ConvertToBicep(json);
        var slug  = Slugify(name ?? "initiative");
        return File(System.Text.Encoding.UTF8.GetBytes(bicep), "text/plain", $"{slug}.bicep");
    }

    /// <summary>Downloads the current initiative as a Bicep assignment template.</summary>
    [HttpGet("download/assignment")]
    public IActionResult DownloadAssignment()
    {
        var (json, name) = DownloadCache.Get();
        if (json is null) return NotFound("No initiative available for download.");
        var bicep = _mergerService.GenerateAssignmentTemplate(json);
        var slug  = Slugify(name ?? "initiative");
        return File(System.Text.Encoding.UTF8.GetBytes(bicep), "text/plain", $"{slug}-assignment.bicep");
    }

    private static string Slugify(string name) =>
        System.Text.RegularExpressions.Regex
            .Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
}
