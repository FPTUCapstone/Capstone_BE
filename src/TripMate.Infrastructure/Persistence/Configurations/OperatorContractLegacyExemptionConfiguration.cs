using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class OperatorContractLegacyExemptionConfiguration : IEntityTypeConfiguration<OperatorContractLegacyExemption>
{
    public void Configure(EntityTypeBuilder<OperatorContractLegacyExemption> builder)
    {
        builder.ToTable("OperatorContractLegacyExemptions", "admin");
        builder.HasKey(x => x.OperatorUserId);
        builder.Property(x => x.OperatorUserId).HasColumnName("operator_user_id").ValueGeneratedNever();
        builder.Property(x => x.GrantedAtUtc).HasColumnName("granted_at").AsUtcDateTime2();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(300);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}