/* UC-31 / MSG205. The booking insert and its inbox notice share one transaction. */
CREATE OR ALTER TRIGGER commercial.TR_ServiceBookings_NotifyProvider
ON commercial.ServiceBookings
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    -- Same recipient as the Provider booking requests workspace (UC-78).
    INSERT INTO dbo.Notifications
        (user_id, channel, type, title, body, related_entity_type, related_entity_id, status, created_at)
    SELECT recipient.user_id, 'InApp', 'MSG205', N'New service booking request',
        CONCAT(N'New booking request SR-', booking.service_booking_id, N' for ', service.name,
            N' on ', CONVERT(char(10), DATEADD(hour, 7, booking.start_datetime), 23),
            N'. Please respond within 24 hours.'),
        'ServiceBooking', booking.service_booking_id, 'Pending', booking.request_submitted_at
    FROM inserted AS booking
    INNER JOIN commercial.Services AS service ON service.service_id = booking.service_id
    INNER JOIN commercial.ProviderPoiLinks AS link ON link.poi_id = service.poi_id
    INNER JOIN commercial.ServiceProviders AS provider ON provider.provider_id = link.provider_id
    INNER JOIN dbo.Users AS recipient ON recipient.user_id = provider.user_id
    WHERE booking.status = 'PendingConfirmation'
      AND booking.request_submitted_at IS NOT NULL
      AND provider.status = 'Active'
      AND recipient.role = 'ServiceProvider'
      AND recipient.status = 'Active'
      AND recipient.must_change_password = 0;
END;
GO
