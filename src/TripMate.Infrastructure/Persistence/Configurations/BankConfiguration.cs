using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class BankConfiguration : IEntityTypeConfiguration<Bank>
{
    public void Configure(EntityTypeBuilder<Bank> builder)
    {
        builder.ToTable("Banks", "payment", table =>
        {
            table.HasCheckConstraint("CK_Banks_Bin", "bank_bin NOT LIKE '%[^0-9]%' AND LEN(bank_bin) = 6");
        });
        builder.HasKey(bank => bank.BankBin);
        builder.Property(bank => bank.BankBin)
            .HasColumnName("bank_bin")
            .HasMaxLength(Payout.BankBinLength)
            .IsFixedLength()
            .IsUnicode(false)
            .ValueGeneratedNever();
        builder.Property(bank => bank.ShortName)
            .HasColumnName("short_name")
            .HasMaxLength(Bank.ShortNameMaxLength)
            .IsRequired();
        builder.Property(bank => bank.OfficialName)
            .HasColumnName("official_name")
            .HasMaxLength(Bank.OfficialNameMaxLength)
            .IsRequired();
        builder.Property(bank => bank.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(bank => bank.ShortName).IsUnique().HasDatabaseName("UX_Banks_ShortName");
        builder.HasIndex(bank => bank.OfficialName).IsUnique().HasDatabaseName("UX_Banks_OfficialName");
    }
}
