using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class SchedulingRequestConfiguration
    : IEntityTypeConfiguration<SchedulingRequest>
{
    public void Configure(EntityTypeBuilder<SchedulingRequest> builder)
    {
        builder.ToTable("SchedulingRequests", "planning");

        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id)
            .HasColumnName("request_id")
            .ValueGeneratedOnAdd();
        builder.Property(request => request.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(request => request.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired();
        builder.Property(request => request.RequestHash)
            .HasColumnName("request_hash")
            .HasMaxLength(SchedulingRequest.RequestHashLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(request => request.StartAtUtc)
            .HasColumnName("start_at")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(request => request.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(SchedulingRequest.TimeZoneIdMaxLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(request => request.StartLatitude)
            .HasColumnName("start_latitude")
            .HasPrecision(9, 6)
            .IsRequired();
        builder.Property(request => request.StartLongitude)
            .HasColumnName("start_longitude")
            .HasPrecision(9, 6)
            .IsRequired();
        builder.Property(request => request.ExplorationLatitude)
            .HasColumnName("destination_latitude")
            .HasPrecision(9, 6)
            .IsRequired();
        builder.Property(request => request.ExplorationLongitude)
            .HasColumnName("destination_longitude")
            .HasPrecision(9, 6)
            .IsRequired();
        builder.Property(request => request.EndPointOfInterestId)
            .HasColumnName("end_poi_id");
        builder.Property(request => request.ReturnToStart)
            .HasColumnName("return_to_start")
            .IsRequired();
        builder.Property(request => request.AvailableMinutes)
            .HasColumnName("available_minutes")
            .IsRequired();
        builder.Property(request => request.TransportMode)
            .HasColumnName("transport_mode")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(request => request.SearchRadiusKm)
            .HasColumnName("search_radius_km")
            .HasPrecision(6, 2)
            .IsRequired();
        builder.Property(request => request.BudgetVnd)
            .HasColumnName("budget")
            .HasPrecision(12, 2);
        builder.Property(request => request.MandatoryPoiIdsJson)
            .HasColumnName("mandatory_poi_ids_json")
            .HasMaxLength(500)
            .IsRequired();
        builder.Property(request => request.RestPreference)
            .HasColumnName("rest_preference")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(request => request.RequestedAtUtc)
            .HasColumnName("requested_at")
            .AsUtcDateTime2()
            .IsRequired();
        builder.Property(request => request.CompletedAtUtc)
            .HasColumnName("completed_at")
            .AsUtcDateTime2();
        builder.Property(request => request.FailureCode)
            .HasColumnName("failure_code")
            .HasMaxLength(100)
            .IsUnicode(false);
        builder.Property(request => request.FailureMessage)
            .HasColumnName("failure_message")
            .HasMaxLength(500);

        builder.HasIndex(request => new { request.TravelerUserId, request.IdempotencyKey })
            .HasDatabaseName("UX_SchedulingRequests_Traveler_Key")
            .IsUnique();
        builder.HasOne(request => request.TravelerUser)
            .WithMany()
            .HasForeignKey(request => request.TravelerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}