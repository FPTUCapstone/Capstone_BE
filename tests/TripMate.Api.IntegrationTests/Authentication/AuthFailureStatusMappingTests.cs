using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// Locks the HTTP status mapping for Firebase token verification failures.
/// An invalid/expired Firebase ID token is an authentication failure and must
/// surface as 401 Unauthorized — not the 400 default of <c>HandleFailure</c>.
/// The response must remain RFC 7807 ProblemDetails with the stable code in
/// <c>extensions.errorCode</c>.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public class AuthFailureStatusMappingTests
{
    [Fact]
    public async Task Post_Register_WithInvalidFirebaseToken_ReturnsUnauthorizedWithTokenInvalidCode()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateJwtClient("not-a-valid-firebase-id-token");

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "traveler@example.com",
            password = "Password123!",
            fullName = "Traveler One",
            phoneNumber = (string?)null,
            acceptedTerms = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString()
            .Should().Be("AUTH_TOKEN_INVALID");
    }

    [Fact]
    public async Task Post_VerifyEmail_WithInvalidFirebaseToken_ReturnsUnauthorizedWithMsg14Code()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateJwtClient("not-a-valid-firebase-id-token");

        var response = await client.PostAsync("/api/v1/auth/verify-email", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString()
            .Should().Be("MSG14");
    }
}
