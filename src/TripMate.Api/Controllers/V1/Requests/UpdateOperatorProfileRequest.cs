using Microsoft.AspNetCore.Mvc;

namespace TripMate.Api.Controllers.V1.Requests;

/// <summary>UC-34 form. Only editable business-profile fields are bound; a tax code, licence number, account
/// email, approval status or commission sent by a client has no property here and is ignored.</summary>
public sealed class UpdateOperatorProfileRequest
{
    [FromForm(Name = "businessName")]
    public string? BusinessName { get; init; }

    [FromForm(Name = "businessDescription")]
    public string? BusinessDescription { get; init; }

    [FromForm(Name = "businessAddress")]
    public string? BusinessAddress { get; init; }

    [FromForm(Name = "contactPhone")]
    public string? ContactPhone { get; init; }

    [FromForm(Name = "contactEmail")]
    public string? ContactEmail { get; init; }

    [FromForm(Name = "website")]
    public string? Website { get; init; }

    [FromForm(Name = "logo")]
    public IFormFile? Logo { get; init; }

    /// <summary>Kept as text so an unparsable value becomes a field error instead of a framework 400.</summary>
    [FromForm(Name = "removeLogo")]
    public string? RemoveLogo { get; init; }
}