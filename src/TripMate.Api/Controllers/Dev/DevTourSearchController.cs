using System.Data.Common;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.DevSearch;
using TripMate.Application.Features.Tours.DevSearch;
using TripMate.Application.Features.Tours.GetCategories;

namespace TripMate.Api.Controllers.Dev;

[AllowAnonymous] // Separate dev access key is mandatory before resolving search services or accessing SQL.
[Route("api/dev/tours")]
public sealed class DevTourSearchController(ISender sender, IConfiguration config, IHostEnvironment environment,
    ILogger<DevTourSearchController> logger) : ApiControllerBase(sender)
{
    private IActionResult? Denial()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-TM70-Trace-Id"] = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        if (!DevTourAccess.Enabled(environment.EnvironmentName, config.GetValue<bool>("DevTourSearch:Enabled"))) return NotFound();
        if (Request.Headers[DevTourAccess.Header].Count != 1 || !DevTourAccess.Allows(environment.EnvironmentName, true,
            config["DevTourSearch:Key"], Request.Headers[DevTourAccess.Header][0])) return Unauthorized();
        return null;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(CancellationToken cancellationToken)
    {
        if (Denial() is { } denial) return denial;
        var modes = Request.Query["mode"];
        var mode = modes.Count == 0 ? DevTourSearch.Lexical : modes[0];
        if (modes.Count > 1 || mode is null || !DevTourSearch.Modes.Contains(mode, StringComparer.Ordinal))
            ModelState.AddModelError("mode", "Mode must be lexical, semantic or hybrid.");
        var criteria = new QueryCollection(Request.Query.Where(kv => kv.Key != "mode").ToDictionary(kv => kv.Key, kv => kv.Value));
        if (!TourSearchV2QueryParser.TryParse(criteria, ModelState, out var request)) return ValidationProblem(ModelState);
        try
        {
            var result = await HttpContext.RequestServices.GetRequiredService<DevTourSearch>().SearchAsync(request, mode!, cancellationToken);
            if (result.IsSuccess) return Ok(result.Value);
            logger.LogWarning("Dev search failed {Code} mode {Mode} trace {TraceId}", result.ErrorCode, mode, HttpContext.TraceIdentifier);
            return Failure(result.ErrorCode ?? "DevSearch.DependencyUnavailable", result.ErrorCode == "DevSearch.InvalidInput" ? 400 : 503);
        }
        catch (DbException)
        {
            logger.LogWarning("Dev search SQL dependency failed trace {TraceId}", HttpContext.TraceIdentifier);
            return Failure("DevSearch.SqlUnavailable", 503);
        }
    }

    [HttpGet("categories")]
    public async Task<IActionResult> Categories(CancellationToken cancellationToken)
    {
        if (Denial() is { } denial) return denial;
        var result = await Sender.Send(new GetTourCategoriesQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    private ObjectResult Failure(string code, int status) => Problem(statusCode: status, title: "Search is unavailable. Please try again.",
        extensions: new Dictionary<string, object?> { ["errorCode"] = code });
}