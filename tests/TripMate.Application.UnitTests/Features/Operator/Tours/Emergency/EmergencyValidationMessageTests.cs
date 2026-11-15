using FluentAssertions;

using FluentValidation;

using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Operator.Tours.Emergency.CancelBeforeDeparture;
using TripMate.Application.Features.Operator.Tours.Emergency.EmergencyStopActive;
using TripMate.Application.Features.Operator.Tours.Emergency.GetImpact;
using TripMate.Application.Features.Operator.WeatherSafetyReviews;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Emergency;

public sealed class EmergencyValidationMessageTests
{
    private static readonly string ImpactVersion = new('a', 64);

    private static void Rejects<T>(AbstractValidator<T> validator, T request, string message) =>
        validator.Validate(request).Errors.Select(error => error.ErrorMessage).Should().Contain(message);

    [Fact]
    public void AcknowledgementNote_OverTheLimit() => Rejects(
        new AcknowledgeWeatherSafetyReviewCommandValidator(),
        new AcknowledgeWeatherSafetyReviewCommand(1, new string('a', 1001)),
        "Ghi chú xác nhận nội bộ tối đa 1000 ký tự.");

    [Fact]
    public void Cancel_WithoutIdempotencyKey() => Rejects(
        new CancelBeforeDepartureCommandValidator(),
        new CancelBeforeDepartureCommand(
            1, 1, Guid.Empty, ImpactVersion, EmergencyTourReason.WeatherEmergency, "Bão đổ bộ"),
        "Thiếu Idempotency-Key hợp lệ.");

    [Fact]
    public void Stop_WithMalformedImpactVersion() => Rejects(
        new EmergencyStopActiveCommandValidator(),
        new EmergencyStopActiveCommand(
            1, 1, Guid.NewGuid(), "not-a-version", "password", EmergencyTourReason.WeatherEmergency,
            "Bão đổ bộ", "Tập trung tại sảnh", "0905123456"),
        "Phiên bản tác động không hợp lệ.");

    [Fact]
    public void Cancel_WithAnEmptyReason_UsesSrsMsg123() => Rejects(
        new CancelBeforeDepartureCommandValidator(),
        new CancelBeforeDepartureCommand(
            1, 1, Guid.NewGuid(), ImpactVersion, EmergencyTourReason.WeatherEmergency, " "),
        EmergencyTourMessages.ReasonRequired);

    [Fact]
    public void Stop_WithAnEmptyReason_UsesSrsMsg123() => Rejects(
        new EmergencyStopActiveCommandValidator(),
        new EmergencyStopActiveCommand(
            1, 1, Guid.NewGuid(), ImpactVersion, "password", EmergencyTourReason.WeatherEmergency,
            "", "Tập trung tại sảnh", "0905123456"),
        EmergencyTourMessages.ReasonRequired);

    [Fact]
    public void SrsMessages_MatchTheirMessageTableMeaning()
    {
        EmergencyTourMessages.ReasonRequired.Should().Be("Vui lòng nhập lý do.");
        EmergencyTourMessages.PermissionDenied.Should().Be("Bạn không có quyền truy cập chức năng này.");
        EmergencyTourMessages.StatusNotAllowed.Should()
            .Be("Thao tác này không khả dụng với trạng thái hiện tại của lịch khởi hành.");
    }

    [Fact]
    public void Impact_WithInvalidIds()
    {
        var query = new GetEmergencyTourImpactQuery(0, 0);

        Rejects(new GetEmergencyTourImpactQueryValidator(), query, "Mã tour không hợp lệ.");
        Rejects(new GetEmergencyTourImpactQueryValidator(), query, "Mã lịch khởi hành không hợp lệ.");
    }
}