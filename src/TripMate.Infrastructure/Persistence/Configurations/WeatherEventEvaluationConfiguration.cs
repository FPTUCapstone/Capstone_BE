using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class WeatherEventEvaluationConfiguration : IEntityTypeConfiguration<WeatherEventEvaluation>
{
    public void Configure(EntityTypeBuilder<WeatherEventEvaluation> builder)
    {
        builder.ToTable("WeatherEventEvaluations", "trip");
        builder.HasKey(evaluation => evaluation.Id);
        builder.Property(evaluation => evaluation.Id).HasColumnName("evaluation_id").ValueGeneratedOnAdd();
        builder.Property(evaluation => evaluation.Provider).HasColumnName("provider")
            .HasMaxLength(WeatherEventEvaluation.ProviderMaxLength).IsUnicode(false).IsRequired();
        builder.Property(evaluation => evaluation.ProviderEventId).HasColumnName("provider_event_id")
            .HasMaxLength(WeatherEventEvaluation.ProviderEventIdMaxLength).IsUnicode(false).IsRequired();
        builder.Property(evaluation => evaluation.TourScheduleId).HasColumnName("tour_schedule_id").IsRequired();
        builder.Property(evaluation => evaluation.DecisionOutcome).HasColumnName("decision_outcome")
            .HasMaxLength(WeatherEventEvaluation.DecisionOutcomeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(evaluation => evaluation.ReasonCode).HasColumnName("reason_code")
            .HasMaxLength(WeatherEventEvaluation.ReasonCodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(evaluation => evaluation.ReasonDetails).HasColumnName("reason_details")
            .HasMaxLength(WeatherEventEvaluation.ReasonDetailsMaxLength);
        builder.Property(evaluation => evaluation.EvaluatedAtUtc).HasColumnName("evaluated_at").AsUtcDateTime2().IsRequired();

        builder.HasOne(evaluation => evaluation.TourSchedule).WithMany()
            .HasForeignKey(evaluation => evaluation.TourScheduleId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(evaluation => new { evaluation.Provider, evaluation.ProviderEventId, evaluation.TourScheduleId }).IsUnique()
            .HasDatabaseName("UX_WeatherEventEvaluations_ProviderEventSchedule");
    }
}
