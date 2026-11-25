using System.Linq.Expressions;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.Realtime;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.DirectChat;

public sealed class DirectChatNotifierTests
{
    [Fact]
    public async Task RecipientQueryFailure_DoesNotEscapeOrAttemptDelivery()
    {
        var query = new FailingQuery(new InvalidOperationException("Recipient query unavailable."));
        await using var db = CreateDbContext(query);
        var transport = new UnusedTransport();
        var notifier = new SignalRDirectChatNotifier(db, transport, NullLogger<SignalRDirectChatNotifier>.Instance);

        var failure = await Record.ExceptionAsync(() => notifier.NotifyAsync(1, true, default));

        Assert.True(query.Attempted);
        Assert.Null(failure);
        Assert.False(transport.Accessed);
    }

    [Fact]
    public async Task RecipientQueryCancellation_StillPropagates()
    {
        var cancellation = new OperationCanceledException();
        var query = new FailingQuery(cancellation);
        await using var db = CreateDbContext(query);
        var transport = new UnusedTransport();
        var notifier = new SignalRDirectChatNotifier(db, transport, NullLogger<SignalRDirectChatNotifier>.Instance);

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => notifier.NotifyAsync(1, true, default));

        Assert.Same(cancellation, failure);
        Assert.True(query.Attempted);
        Assert.False(transport.Accessed);
    }

    private static ApplicationDbContext CreateDbContext(IInterceptor interceptor) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"direct-chat-notifier-{Guid.NewGuid():N}")
            .AddInterceptors(interceptor).Options);

    private sealed class FailingQuery(Exception failure) : IQueryExpressionInterceptor
    {
        public bool Attempted { get; private set; }

        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            Attempted = true;
            throw failure;
        }
    }

    private sealed class UnusedTransport : IHubContext<DirectChatHub>
    {
        public bool Accessed { get; private set; }
        public IHubClients Clients
        {
            get
            {
                Accessed = true;
                throw new InvalidOperationException("Delivery must not run after recipient lookup fails.");
            }
        }
        public IGroupManager Groups => throw new NotSupportedException();
    }
}