namespace TripMate.Api.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class AuthorizationProblemDetailsAttribute(
    string unauthorizedErrorCode,
    string unauthorizedTitle,
    string forbiddenErrorCode,
    string forbiddenTitle) : Attribute
{
    public string UnauthorizedErrorCode { get; } = unauthorizedErrorCode;
    public string UnauthorizedTitle { get; } = unauthorizedTitle;
    public string ForbiddenErrorCode { get; } = forbiddenErrorCode;
    public string ForbiddenTitle { get; } = forbiddenTitle;
}