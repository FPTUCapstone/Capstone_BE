namespace TripMate.Api.Controllers.V1.Requests;

/// <summary>Optional body for reach and skip; omitted or null uses the server clock.</summary>
public sealed record NavigationProgressRequest(DateTimeOffset? OccurredAtUtc);

/// <summary>Body for leaving the Exploring phase; <c>state</c> must be <c>Navigating</c>.</summary>
public sealed record DepartNavigationSessionRequest(string? State, DateTimeOffset? OccurredAtUtc);