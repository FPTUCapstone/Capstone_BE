using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class CheckInLogConfiguration : IEntityTypeConfiguration<CheckInLog>
{
    public const string IndexTicket = "IX_CheckInLogs_Ticket";

    public void Configure(EntityTypeBuilder<CheckInLog> builder)
    {
        builder.ToTable("CheckInLogs", "commerce");
        builder.HasKey(log => log.Id);
        builder.Property(log => log.Id).HasColumnName("checkin_log_id").ValueGeneratedOnAdd();
        builder.Property(log => log.TicketId).HasColumnName("ticket_id").IsRequired();
        builder.Property(log => log.QrCodeId).HasColumnName("qr_code_id");
        builder.Property(log => log.CheckedInBy).HasColumnName("checked_in_by");
        builder.Property(log => log.ScanResult).HasColumnName("scan_result")
            .HasConversion<string>().HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(log => log.Latitude).HasColumnName("latitude").HasPrecision(9, 6);
        builder.Property(log => log.Longitude).HasColumnName("longitude").HasPrecision(9, 6);
        builder.Property(log => log.CheckedInAtUtc).HasColumnName("checked_in_at").AsUtcDateTime2().IsRequired();

        builder.HasOne(log => log.Ticket).WithMany().HasForeignKey(log => log.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(log => log.QrCode).WithMany().HasForeignKey(log => log.QrCodeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(log => log.CheckedInByUser).WithMany().HasForeignKey(log => log.CheckedInBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(log => new { log.TicketId, log.CheckedInAtUtc }, IndexTicket);
    }
}
