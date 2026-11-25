using System.Reflection;

using FluentAssertions;

using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class PoiRemovalAuditTests
{
    [Fact]
    public void PoiDeactivated_HasExactStageAValue()
    {
        AuditActionTypes.PoiDeactivated.Should().Be("POI_DEACTIVATED");
    }

    [Fact]
    public void RecordedOutcome_WithPoiDeactivated_PreservesSuppliedAuditData()
    {
        var occurredAtUtc = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);

        var audit = AuditLog.CreateRecordedOutcome(
            actorUserId: 17,
            actionType: AuditActionTypes.PoiDeactivated,
            affectedEntity: AuditEntityTypes.PointOfInterest,
            affectedEntityId: 42,
            createdAtUtc: occurredAtUtc,
            result: AuditOutcome.Success,
            reason: "Duplicate POI",
            beforeData: "{\"status\":\"Active\"}",
            afterData: "{\"status\":\"Inactive\"}",
            ipAddress: "127.0.0.1");

        audit.ActorUserId.Should().Be(17);
        audit.ActionType.Should().Be(AuditActionTypes.PoiDeactivated);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.PointOfInterest);
        audit.AffectedEntityId.Should().Be(42);
        audit.CreatedAtUtc.Should().Be(occurredAtUtc);
        audit.Result.Should().Be(AuditOutcome.Success);
        audit.Reason.Should().Be("Duplicate POI");
        audit.BeforeData.Should().Be("{\"status\":\"Active\"}");
        audit.AfterData.Should().Be("{\"status\":\"Inactive\"}");
        audit.IpAddress.Should().Be("127.0.0.1");
    }

    [Fact]
    public void StageA_DoesNotExposePoiDeletedVocabulary()
    {
        var publicConstants = typeof(AuditActionTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly)
            .ToArray();

        publicConstants.Should().NotContain(field => field.Name == "PoiDeleted");
        publicConstants.Should().NotContain(field =>
            string.Equals(field.GetRawConstantValue() as string, "POI_DELETED", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingPoiCreateVocabulary_RemainsUnchanged()
    {
        AuditActionTypes.PoiCreate.Should().Be("POI_CREATE");
    }
}