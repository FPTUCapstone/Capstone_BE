namespace TripMate.Api.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class AuthorizationFailureAuditAttribute(
    string actionType,
    string affectedEntity,
    string affectedEntityIdRouteValueName) : Attribute
{
    public string ActionType { get; } = actionType;

    public string AffectedEntity { get; } = affectedEntity;

    public string AffectedEntityIdRouteValueName { get; } = affectedEntityIdRouteValueName;
}