using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class RouteSegmentConfiguration : IEntityTypeConfiguration<RouteSegment>
{
    public void Configure(EntityTypeBuilder<RouteSegment> builder)
    {
        builder.ToTable("RouteSegments", "catalog", table => table.UseSqlOutputClause(false));
        builder.HasKey(route => route.Id);
        builder.Property(route => route.Id).HasColumnName("segment_id").ValueGeneratedOnAdd();
        builder.Property(route => route.FromPoiId).HasColumnName("from_poi_id");
        builder.Property(route => route.ToPoiId).HasColumnName("to_poi_id");
        builder.HasOne(route => route.FromPoi).WithMany().HasForeignKey(route => route.FromPoiId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(route => route.ToPoi).WithMany().HasForeignKey(route => route.ToPoiId).OnDelete(DeleteBehavior.NoAction);
        builder.Property(route => route.TransportMode).HasColumnName("transport_mode").HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(route => route.DistanceKm).HasColumnName("distance_km").HasPrecision(6, 2);
        builder.Property(route => route.EstimatedDurationMinutes).HasColumnName("est_duration_minutes");
        builder.Property(route => route.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        builder.Property(route => route.RouteNote).HasColumnName("route_note").HasMaxLength(RouteSegment.NoteMaxLength);
        builder.Property(route => route.CreatedById).HasColumnName("created_by");
        builder.HasOne<TripMate.Domain.Entities.User>().WithMany().HasForeignKey(route => route.CreatedById).OnDelete(DeleteBehavior.NoAction);
        builder.Property(route => route.IdempotencyKey).HasColumnName("idempotency_key");
        builder.Property(route => route.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.HasIndex(route => new { route.FromPoiId, route.ToPoiId, route.TransportMode }).IsUnique().HasDatabaseName("UQ_RouteSegments");
        builder.HasIndex(route => new { route.CreatedById, route.IdempotencyKey }).IsUnique()
            .HasFilter("[idempotency_key] IS NOT NULL").HasDatabaseName("UX_RouteSegments_ActorOperation");
    }
}