using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TicketQrCodeConfiguration : IEntityTypeConfiguration<TicketQrCode>
{
    /// <summary>Filtered unique index from tripmate_schema_v7.sql; at most one Active QR per ticket.</summary>
    public const string OneActivePerTicketIndexName = "UX_TicketQrCodes_OneActivePerTicket";

    public void Configure(EntityTypeBuilder<TicketQrCode> builder)
    {
        builder.ToTable("TicketQrCodes", "commerce");
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).HasColumnName("qr_code_id").ValueGeneratedOnAdd();
        builder.Property(code => code.TicketId).HasColumnName("ticket_id").IsRequired();
        builder.Property(code => code.QrToken).HasColumnName("qr_token")
            .HasMaxLength(TicketQrCode.QrTokenMaxLength).IsRequired();
        builder.Property(code => code.Signature).HasColumnName("signature")
            .HasMaxLength(TicketQrCode.SignatureMaxLength).IsRequired();
        builder.Property(code => code.Status).HasColumnName("status").HasConversion<string>()
            .HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(code => code.IssuedAtUtc).HasColumnName("issued_at").AsUtcDateTime2().IsRequired();
        builder.Property(code => code.ExpiresAtUtc).HasColumnName("expires_at").AsUtcDateTime2();
        builder.Property(code => code.ConsumedAtUtc).HasColumnName("consumed_at").AsUtcDateTime2();
        builder.HasOne<Ticket>().WithMany().HasForeignKey(code => code.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(code => code.TicketId).IsUnique()
            .HasFilter($"[status] = '{nameof(TicketQrCodeStatus.Active)}'")
            .HasDatabaseName(OneActivePerTicketIndexName);
        builder.HasIndex(code => code.QrToken).IsUnique();
    }
}