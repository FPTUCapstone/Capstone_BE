using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TravelerRefundAccountConfiguration : IEntityTypeConfiguration<TravelerRefundAccount>
{
    public void Configure(EntityTypeBuilder<TravelerRefundAccount> builder)
    {
        builder.ToTable("TravelerRefundAccounts", "payment");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(entity => entity.BankCode).HasColumnName("bank_code").HasMaxLength(DirectRefundCase.RecipientBankCodeMaxLength).IsRequired();
        builder.Property(entity => entity.AccountNumberProtected).HasColumnName("account_number_protected").HasMaxLength(TravelerRefundAccount.ProtectedValueMaxLength).IsRequired();
        builder.Property(entity => entity.AccountHolderProtected).HasColumnName("account_holder_protected").HasMaxLength(TravelerRefundAccount.ProtectedValueMaxLength).IsRequired();
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();
        builder.HasOne<User>().WithOne().HasForeignKey<TravelerRefundAccount>(entity => entity.Id).OnDelete(DeleteBehavior.Restrict);
    }
}