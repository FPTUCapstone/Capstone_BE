using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Persistence.Configurations;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class TicketPersistenceModelTests
{
    [Fact]
    public void Ticket_MapsEveryPropertyToTheV7Columns()
    {
        var entity = Model().FindEntityType(typeof(Ticket))!;

        entity.GetSchema().Should().Be("commerce");
        entity.GetTableName().Should().Be("Tickets");
        ColumnOf(entity, nameof(Ticket.Id)).Should().Be("ticket_id");
        ColumnOf(entity, nameof(Ticket.BookingId)).Should().Be("booking_id");
        ColumnOf(entity, nameof(Ticket.ParticipantId)).Should().Be("participant_id");
        ColumnOf(entity, nameof(Ticket.TicketCode)).Should().Be("ticket_code");
        ColumnOf(entity, nameof(Ticket.Status)).Should().Be("status");
        ColumnOf(entity, nameof(Ticket.IssuedAtUtc)).Should().Be("issued_at");
        ColumnOf(entity, nameof(Ticket.ExpiresAtUtc)).Should().Be("expires_at");
        ColumnOf(entity, nameof(Ticket.CheckedInAtUtc)).Should().Be("checked_in_at");
        entity.FindProperty(nameof(Ticket.TicketCode))!.GetMaxLength().Should().Be(Ticket.TicketCodeMaxLength);
        entity.FindProperty(nameof(Ticket.Status))!.GetProviderClrType().Should().Be<string>();
    }

    [Fact]
    public void Ticket_MapsEveryTimestampAsUtcDateTime2()
    {
        var entity = Model().FindEntityType(typeof(Ticket))!;

        foreach (var propertyName in new[]
                 {
                     nameof(Ticket.IssuedAtUtc), nameof(Ticket.ExpiresAtUtc), nameof(Ticket.CheckedInAtUtc),
                 })
        {
            entity.FindProperty(propertyName)!.GetValueConverter().Should().NotBeNull(
                $"{propertyName} must persist DateTimeOffset as UTC DATETIME2");
        }
    }

    [Fact]
    public void Ticket_HasOneBookingRelationshipAndOneTicketPerBooking()
    {
        var entity = Model().FindEntityType(typeof(Ticket))!;

        var foreignKey = entity.GetForeignKeys().Should().ContainSingle().Subject;
        foreignKey.PrincipalEntityType.ClrType.Should().Be<Booking>();
        foreignKey.Properties.Select(property => property.Name).Should().Equal(nameof(Ticket.BookingId));
        foreignKey.DependentToPrincipal!.Name.Should().Be(nameof(Ticket.Booking));
        foreignKey.PrincipalToDependent!.Name.Should().Be(nameof(Booking.Tickets));

        var index = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == TicketConfiguration.OneTicketPerBookingIndexName);
        index.IsUnique.Should().BeTrue();
        index.Properties.Select(property => property.Name).Should().Equal(nameof(Ticket.BookingId));
        entity.GetIndexes().Single(candidate => candidate.Properties.Single().Name == nameof(Ticket.TicketCode))
            .IsUnique.Should().BeTrue();
    }

    [Fact]
    public void TicketQrCode_MapsEveryPropertyToTheV7Columns()
    {
        var entity = Model().FindEntityType(typeof(TicketQrCode))!;

        entity.GetSchema().Should().Be("commerce");
        entity.GetTableName().Should().Be("TicketQrCodes");
        ColumnOf(entity, nameof(TicketQrCode.Id)).Should().Be("qr_code_id");
        ColumnOf(entity, nameof(TicketQrCode.TicketId)).Should().Be("ticket_id");
        ColumnOf(entity, nameof(TicketQrCode.QrToken)).Should().Be("qr_token");
        ColumnOf(entity, nameof(TicketQrCode.Signature)).Should().Be("signature");
        ColumnOf(entity, nameof(TicketQrCode.Status)).Should().Be("status");
        ColumnOf(entity, nameof(TicketQrCode.IssuedAtUtc)).Should().Be("issued_at");
        ColumnOf(entity, nameof(TicketQrCode.ExpiresAtUtc)).Should().Be("expires_at");
        ColumnOf(entity, nameof(TicketQrCode.ConsumedAtUtc)).Should().Be("consumed_at");
        entity.FindProperty(nameof(TicketQrCode.QrToken))!.GetMaxLength().Should().Be(TicketQrCode.QrTokenMaxLength);
        entity.FindProperty(nameof(TicketQrCode.Signature))!.GetMaxLength()
            .Should().Be(TicketQrCode.SignatureMaxLength);
        entity.FindProperty(nameof(TicketQrCode.Status))!.GetProviderClrType().Should().Be<string>();

        foreach (var propertyName in new[]
                 {
                     nameof(TicketQrCode.IssuedAtUtc), nameof(TicketQrCode.ExpiresAtUtc),
                     nameof(TicketQrCode.ConsumedAtUtc),
                 })
        {
            entity.FindProperty(propertyName)!.GetValueConverter().Should().NotBeNull(
                $"{propertyName} must persist DateTimeOffset as UTC DATETIME2");
        }
    }

    [Fact]
    public void TicketQrCode_AllowsOneActiveCodePerTicketAndUniqueTokens()
    {
        var entity = Model().FindEntityType(typeof(TicketQrCode))!;

        var foreignKey = entity.GetForeignKeys().Should().ContainSingle().Subject;
        foreignKey.PrincipalEntityType.ClrType.Should().Be<Ticket>();
        foreignKey.Properties.Select(property => property.Name).Should().Equal(nameof(TicketQrCode.TicketId));

        var oneActive = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == TicketQrCodeConfiguration.OneActivePerTicketIndexName);
        oneActive.IsUnique.Should().BeTrue();
        oneActive.GetFilter().Should().Be("[status] = 'Active'");
        oneActive.Properties.Select(property => property.Name).Should().Equal(nameof(TicketQrCode.TicketId));

        entity.GetIndexes().Single(candidate => candidate.Properties.Single().Name == nameof(TicketQrCode.QrToken))
            .IsUnique.Should().BeTrue();
    }

    private static IModel Model()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        return context.Model;
    }

    private static string? ColumnOf(IEntityType entity, string propertyName) =>
        entity.FindProperty(propertyName)!.GetColumnName();
}
