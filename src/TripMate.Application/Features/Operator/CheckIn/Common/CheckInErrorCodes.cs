namespace TripMate.Application.Features.Operator.CheckIn.Common;

/// <summary>
/// Error codes for UC-43 Tour Operator check-in by QR code or manual booking code.
/// </summary>
public static class CheckInErrorCodes
{
    public const string Unauthorized = "checkin.unauthorized";
    public const string Forbidden = "checkin.forbidden";
    public const string ScheduleNotFound = "checkin.schedule_not_found";
    public const string ScheduleForbidden = "checkin.schedule_forbidden";
    public const string DepartureNotToday = "checkin.departure_not_today";
    public const string InvalidInput = "checkin.invalid_input";
    public const string InvalidQr = "checkin.invalid_qr";
    public const string TicketNotFound = "checkin.ticket_not_found";
    public const string WrongSchedule = "checkin.wrong_schedule";
    public const string BookingCancelled = "checkin.booking_cancelled";
    public const string BookingNotConfirmed = "checkin.booking_not_confirmed";
    public const string AlreadyUsed = "checkin.already_used";
    public const string TicketInvalid = "checkin.ticket_invalid";
}