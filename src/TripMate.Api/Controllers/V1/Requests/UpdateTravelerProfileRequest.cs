using Microsoft.AspNetCore.Mvc;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed class UpdateTravelerProfileRequest
{
    [FromForm(Name = "fullName")]
    public string? FullName { get; init; }

    [FromForm(Name = "phoneNumber")]
    public string? PhoneNumber { get; init; }

    [FromForm(Name = "dateOfBirth")]
    public DateOnly? DateOfBirth { get; init; }

    [FromForm(Name = "avatar")]
    public IFormFile? Avatar { get; init; }

    [FromForm(Name = "removeAvatar")]
    public bool RemoveAvatar { get; init; }
}