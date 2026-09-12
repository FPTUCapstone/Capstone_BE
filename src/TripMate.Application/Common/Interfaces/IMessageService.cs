namespace TripMate.Application.Common.Interfaces;

public interface IMessageService
{
    Task<string?> GetMessageAsync(
        string messageCode,
        CancellationToken cancellationToken = default);

    Task<string?> FormatMessageAsync(
        string messageCode,
        IDictionary<string, string> parameters,
        CancellationToken cancellationToken = default);
}
