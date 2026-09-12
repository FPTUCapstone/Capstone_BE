using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

public class MessageService(
    IApplicationDbContext dbContext,
    IMemoryCache memoryCache)
    : IMessageService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public async Task<string?> GetMessageAsync(
        string messageCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageCode))
        {
            return null;
        }

        var cacheKey = $"msg:{messageCode}";

        if (memoryCache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return cached;
        }

        var template = await dbContext.Messages
            .AsNoTracking()
            .Where(m => m.MessageCode == messageCode)
            .Select(m => m.ContentTemplate)
            .FirstOrDefaultAsync(cancellationToken);

        if (template is not null)
        {
            memoryCache.Set(cacheKey, template, CacheDuration);
        }

        return template;
    }

    public async Task<string?> FormatMessageAsync(
        string messageCode,
        IDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var template = await GetMessageAsync(messageCode, cancellationToken);
        if (template is null)
        {
            return null;
        }

        foreach (var (key, value) in parameters)
        {
            template = template.Replace($"{{{key}}}", value);
        }

        return template;
    }
}
