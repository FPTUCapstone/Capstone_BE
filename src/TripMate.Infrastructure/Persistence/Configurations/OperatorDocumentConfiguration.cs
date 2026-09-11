using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class OperatorDocumentConfiguration : IEntityTypeConfiguration<OperatorDocument>
{
    public void Configure(EntityTypeBuilder<OperatorDocument> builder)
    {
        builder.ToTable("OperatorDocuments", "dbo");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("document_id").ValueGeneratedOnAdd();

        builder.Property(d => d.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(d => d.DocumentType).HasColumnName("document_type").HasConversion<string>().HasMaxLength(30);
        builder.Property(d => d.FileUrl).HasColumnName("file_url").HasMaxLength(500).IsRequired();
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.UploadedAtUtc).HasColumnName("uploaded_at").AsUtcDateTime2();

        builder.HasIndex(d => d.OperatorUserId).HasDatabaseName("IX_OperatorDocuments_Operator");
    }
}
