using TripMate.Domain.Common;
namespace TripMate.Domain.Entities;

public sealed class GroupMessageEditOperation : BaseEntity
{
    private GroupMessageEditOperation() { }
    public GroupMessageEditOperation(GroupMessage message, long actorId, Guid clientRequestId,
        long expectedRevision, string content, string? metadataJson)
    {
        if (actorId <= 0 || clientRequestId == Guid.Empty || expectedRevision < 0 || string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Valid edit request required.");
        Message = message; ActorId = actorId; ClientRequestId = clientRequestId;
        ExpectedRevision = expectedRevision; Content = content; MetadataJson = metadataJson;
    }
    public long MessageId { get; private set; }
    public GroupMessage Message { get; private set; } = null!;
    public long ActorId { get; private set; }
    public Guid ClientRequestId { get; private set; }
    public long ExpectedRevision { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public string? MetadataJson { get; private set; }
}