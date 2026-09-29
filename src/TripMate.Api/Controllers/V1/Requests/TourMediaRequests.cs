using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Http;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed class UploadTourMediaRequest
{
    [Required]
    public IFormFile? File { get; init; }

    [StringLength(500)]
    public string? Caption { get; init; }

    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string AltText { get; init; } = string.Empty;

    public bool IsPrimary { get; init; }
}

public sealed class UpdateTourMediaMetadataRequest
{
    [StringLength(500)]
    public string? Caption { get; init; }

    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string AltText { get; init; } = string.Empty;
}

public sealed class ReorderTourMediaRequest
{
    [Required]
    public IReadOnlyList<long> MediaIds { get; init; } = [];

    public long? PrimaryMediaId { get; init; }
}