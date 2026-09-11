namespace TripMate.Application.Common.Interfaces;

public interface ICurrentUserService
{
    long? UserId { get; }

    string? Role { get; }
}