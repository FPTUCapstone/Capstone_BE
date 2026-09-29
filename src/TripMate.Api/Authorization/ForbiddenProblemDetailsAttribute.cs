namespace TripMate.Api.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class ForbiddenProblemDetailsAttribute(string errorCode, string title) : Attribute
{
    public string ErrorCode { get; } = errorCode;

    public string Title { get; } = title;
}