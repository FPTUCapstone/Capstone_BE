/*
    UC-46 local/UAT fixtures for operator.demo@tripmate.local.

    Covers the real settlement calculation shape:
    - Completed + paid bookings across closed and open Vietnam settlement months.
    - Payment-level 10% commission snapshots, discounts, and processed partial refunds.
    - One settled, one rejected, and one cancelled historical payout.
    - A below-minimum closed month, requestable closed months, and an open current month.

    This script is deliberately idempotent. It removes only rows created by this script
    (UC46-DEMO codes/title/email prefix) and must only run against a local/UAT database.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT LIKE N'%TripMate%'
    THROW 51000, N'UC-46 demo seed refused: expected a local/UAT TripMate database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @operatorId BIGINT = (
        SELECT user_id FROM dbo.Users WHERE email = N'operator.demo@tripmate.local');
    IF @operatorId IS NULL
        THROW 51000, N'UC-46 demo seed requires operator.demo@tripmate.local.', 1;

    /* Remove only a prior execution of this fixture, children before parents. */
    DELETE adjustment
    FROM payment.PayoutAdjustments AS adjustment
    JOIN payment.Refunds AS refund ON refund.refund_id = adjustment.source_refund_id
    JOIN commerce.Bookings AS booking ON booking.booking_id = refund.booking_id
    WHERE booking.booking_code LIKE 'UC46-DEMO-%';

    DELETE refundItem
    FROM payment.PayoutRefundItems AS refundItem
    JOIN payment.Payouts AS payout ON payout.payout_id = refundItem.payout_id
    WHERE payout.payout_code LIKE 'PO-UC46-%';

    DELETE item
    FROM payment.PayoutItems AS item
    JOIN payment.Payouts AS payout ON payout.payout_id = item.payout_id
    WHERE payout.payout_code LIKE 'PO-UC46-%';

    DELETE FROM payment.Payouts WHERE payout_code LIKE 'PO-UC46-%';

    DELETE refund
    FROM payment.Refunds AS refund
    JOIN commerce.Bookings AS booking ON booking.booking_id = refund.booking_id
    WHERE booking.booking_code LIKE 'UC46-DEMO-%';

    DELETE payment
    FROM payment.PaymentTransactions AS payment
    JOIN commerce.Bookings AS booking ON booking.booking_id = payment.booking_id
    WHERE booking.booking_code LIKE 'UC46-DEMO-%';

    DELETE FROM commerce.Bookings WHERE booking_code LIKE 'UC46-DEMO-%';

    DELETE schedule
    FROM commerce.TourSchedules AS schedule
    JOIN commerce.Tours AS tour ON tour.tour_id = schedule.tour_id
    WHERE tour.title = N'[UC46 DEMO] Payout settlement financial fixtures';

    DELETE FROM commerce.Tours WHERE title = N'[UC46 DEMO] Payout settlement financial fixtures';

    DELETE FROM dbo.TravelerProfiles WHERE user_id IN (
        SELECT user_id FROM dbo.Users WHERE email LIKE N'uc46.demo.traveler%@tripmate.local');
    DELETE FROM dbo.Users WHERE email LIKE N'uc46.demo.traveler%@tripmate.local';
    DELETE FROM dbo.Users WHERE email IN (N'uc46.demo.staff@tripmate.local', N'uc46.demo.admin@tripmate.local');

    /* A non-sensitive local-only beneficiary snapshot, sufficient for the request precondition. */
    UPDATE dbo.OperatorProfiles
    SET bank_name = N'Ngân hàng Demo',
        account_holder_name = N'UC46 Demo Operator',
        account_number_masked = '****4600',
        account_number_protected = N'UC46-local-fixture-not-a-real-account',
        bank_account_updated_at = SYSUTCDATETIME(),
        updated_at = SYSUTCDATETIME()
    WHERE user_id = @operatorId;

    DECLARE @staffId BIGINT, @adminId BIGINT;
    INSERT dbo.Users (role, email, full_name, status, email_verified_at, created_at, updated_at)
    VALUES ('Staff', N'uc46.demo.staff@tripmate.local', N'UC46 Demo Staff', 'Active', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
    SET @staffId = SCOPE_IDENTITY();
    INSERT dbo.Users (role, email, full_name, status, email_verified_at, created_at, updated_at)
    VALUES ('Administrator', N'uc46.demo.admin@tripmate.local', N'UC46 Demo Administrator', 'Active', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
    SET @adminId = SCOPE_IDENTITY();

    DECLARE @travelerIds TABLE (ordinal INT PRIMARY KEY, user_id BIGINT NOT NULL);
    DECLARE @travelerOrdinal INT = 1;
    WHILE @travelerOrdinal <= 8
    BEGIN
        DECLARE @travelerId BIGINT;
        INSERT dbo.Users (role, email, full_name, status, email_verified_at, created_at, updated_at)
        VALUES ('Traveler', CONCAT(N'uc46.demo.traveler', @travelerOrdinal, N'@tripmate.local'),
            CONCAT(N'UC46 Demo Traveler ', @travelerOrdinal), 'Active', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
        SET @travelerId = SCOPE_IDENTITY();
        INSERT dbo.TravelerProfiles (user_id, updated_at) VALUES (@travelerId, SYSUTCDATETIME());
        INSERT @travelerIds (ordinal, user_id) VALUES (@travelerOrdinal, @travelerId);
        SET @travelerOrdinal += 1;
    END;

    DECLARE @tourId BIGINT;
    INSERT commerce.Tours (
        operator_user_id, version_root_tour_id, version_no, category_id, title, description,
        destination, base_price, duration_days, status, published_at, created_at, updated_at)
    VALUES (
        @operatorId, NULL, 1, NULL, N'[UC46 DEMO] Payout settlement financial fixtures',
        N'Local financial fixtures for UC-44 revenue and UC-46 payout settlement validation.',
        N'Đà Nẵng', 1200000, 1, 'Approved', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
    SET @tourId = SCOPE_IDENTITY();

    DECLARE @months TABLE (
        month_no INT PRIMARY KEY,
        schedule_id BIGINT NOT NULL,
        schedule_end DATETIME2 NOT NULL,
        completed_count INT NOT NULL,
        base_amount DECIMAL(12,2) NOT NULL,
        create_refunds BIT NOT NULL,
        terminal_payout_status VARCHAR(24) NULL);

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-03-20T01:00:00', '2026-03-20T10:00:00', N'UC46 Demo - March', 100, 6, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (3, SCOPE_IDENTITY(), '2026-03-20T10:00:00', 6, 1000000, 0, 'Settled');

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-04-20T01:00:00', '2026-04-20T10:00:00', N'UC46 Demo - April', 100, 6, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (4, SCOPE_IDENTITY(), '2026-04-20T10:00:00', 6, 1000000, 0, 'Rejected');

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-05-20T01:00:00', '2026-05-20T10:00:00', N'UC46 Demo - May', 100, 6, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (5, SCOPE_IDENTITY(), '2026-05-20T10:00:00', 6, 1000000, 0, 'Cancelled');

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-06-20T01:00:00', '2026-06-20T10:00:00', N'UC46 Demo - June', 100, 18, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (6, SCOPE_IDENTITY(), '2026-06-20T10:00:00', 18, 850000, 1, NULL);

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-07-20T01:00:00', '2026-07-20T10:00:00', N'UC46 Demo - July', 100, 24, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (7, SCOPE_IDENTITY(), '2026-07-20T10:00:00', 24, 1100000, 1, NULL);

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-08-20T01:00:00', '2026-08-20T10:00:00', N'UC46 Demo - August below minimum', 100, 10, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (8, SCOPE_IDENTITY(), '2026-08-20T10:00:00', 10, 300000, 0, NULL);

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-09-20T01:00:00', '2026-09-20T10:00:00', N'UC46 Demo - September', 100, 20, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (9, SCOPE_IDENTITY(), '2026-09-20T10:00:00', 20, 1200000, 1, NULL);

    INSERT commerce.TourSchedules (tour_id, start_datetime, end_datetime, meeting_point, total_capacity, reserved_capacity, status, created_at)
    VALUES (@tourId, '2026-10-15T01:00:00', '2026-10-15T10:00:00', N'UC46 Demo - October open', 100, 12, 'Completed', SYSUTCDATETIME());
    INSERT @months VALUES (10, SCOPE_IDENTITY(), '2026-10-15T10:00:00', 12, 1000000, 0, NULL);

    DECLARE @monthNo INT, @scheduleId BIGINT, @scheduleEnd DATETIME2, @completedCount INT,
        @baseAmount DECIMAL(12,2), @createRefunds BIT, @terminalStatus VARCHAR(24), @index INT,
        @bookingId BIGINT, @paymentId BIGINT, @travelerUserId BIGINT, @discount DECIMAL(12,2),
        @paidAmount DECIMAL(12,2), @bookingCode VARCHAR(30), @orderCode BIGINT = 964600000;

    DECLARE month_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT month_no, schedule_id, schedule_end, completed_count, base_amount, create_refunds, terminal_payout_status
        FROM @months ORDER BY month_no;
    OPEN month_cursor;
    FETCH NEXT FROM month_cursor INTO @monthNo, @scheduleId, @scheduleEnd, @completedCount, @baseAmount, @createRefunds, @terminalStatus;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @index = 1;
        WHILE @index <= @completedCount
        BEGIN
            SELECT @travelerUserId = user_id FROM @travelerIds WHERE ordinal = ((@index - 1) % 8) + 1;
            SET @discount = CASE WHEN @monthNo IN (6, 7, 9) AND @index % 5 = 0 THEN 50000 ELSE 0 END;
            SET @paidAmount = @baseAmount - @discount;
            SET @bookingCode = CONCAT('UC46-DEMO-', @monthNo, '-', RIGHT('000' + CAST(@index AS VARCHAR(3)), 3));

            INSERT commerce.Bookings (
                booking_code, traveler_user_id, tour_schedule_id, itinerary_id, voucher_id, quantity,
                unit_price, discount_amount, total_amount, status, payment_status, booked_at, payment_expires_at)
            VALUES (
                @bookingCode, @travelerUserId, @scheduleId, NULL, NULL, 1,
                @baseAmount, @discount, @paidAmount, 'Completed', 'Paid',
                DATEADD(DAY, -7, @scheduleEnd), DATEADD(DAY, -6, @scheduleEnd));
            SET @bookingId = SCOPE_IDENTITY();

            SET @orderCode += 1;
            INSERT payment.PaymentTransactions (
                booking_id, gateway, gateway_transaction_ref, amount, currency, transaction_type, status,
                raw_response, created_at, order_code, succeeded_at, commission_rate_applied, updated_at)
            VALUES (
                @bookingId, 'Mock', CONCAT('UC46-PAY-', @bookingCode), @paidAmount, 'VND', 'Payment', 'Success',
                N'{"fixture":"UC46"}', DATEADD(DAY, -7, @scheduleEnd), @orderCode,
                DATEADD(DAY, -6, @scheduleEnd), 10.00, DATEADD(DAY, -6, @scheduleEnd));
            SET @paymentId = SCOPE_IDENTITY();

            IF @createRefunds = 1 AND @index % 7 = 0
            BEGIN
                INSERT payment.Refunds (
                    booking_id, transaction_id, reason, amount, initiated_by, status, requested_at, processed_at)
                VALUES (
                    @bookingId, @paymentId, N'UC46 demo partial refund already processed', @paidAmount / 4,
                    'Administrator', 'Processed', DATEADD(DAY, -4, @scheduleEnd), DATEADD(DAY, -3, @scheduleEnd));
            END;

            SET @index += 1;
        END;
        FETCH NEXT FROM month_cursor INTO @monthNo, @scheduleId, @scheduleEnd, @completedCount, @baseAmount, @createRefunds, @terminalStatus;
    END;
    CLOSE month_cursor;
    DEALLOCATE month_cursor;

    /* Non-eligible controls: confirmed-paid, pending-payment, and hold-period booking. */
    DECLARE @octoberScheduleId BIGINT = (SELECT schedule_id FROM @months WHERE month_no = 10);
    DECLARE @controlIndex INT = 1;
    WHILE @controlIndex <= 6
    BEGIN
        SELECT @travelerUserId = user_id FROM @travelerIds WHERE ordinal = ((@controlIndex - 1) % 8) + 1;
        SET @bookingCode = CONCAT('UC46-DEMO-CONTROL-', @controlIndex);
        INSERT commerce.Bookings (
            booking_code, traveler_user_id, tour_schedule_id, itinerary_id, voucher_id, quantity,
            unit_price, discount_amount, total_amount, status, payment_status, booked_at, payment_expires_at)
        VALUES (
            @bookingCode, @travelerUserId, @octoberScheduleId, NULL, NULL, 1,
            500000, 0, 500000,
            CASE WHEN @controlIndex <= 2 THEN 'Confirmed' WHEN @controlIndex <= 4 THEN 'PendingPayment' ELSE 'Completed' END,
            CASE WHEN @controlIndex <= 2 OR @controlIndex > 4 THEN 'Paid' ELSE 'Unpaid' END,
            '2026-10-14T01:00:00', '2026-10-14T01:30:00');
        SET @bookingId = SCOPE_IDENTITY();
        SET @orderCode += 1;
        INSERT payment.PaymentTransactions (
            booking_id, gateway, amount, currency, transaction_type, status, raw_response, created_at, order_code, succeeded_at, commission_rate_applied, updated_at)
        VALUES (
            @bookingId, 'Mock', 500000, 'VND', 'Payment',
            CASE WHEN @controlIndex <= 2 OR @controlIndex > 4 THEN 'Success' ELSE 'Pending' END,
            N'{"fixture":"UC46-control"}', '2026-10-14T01:00:00', @orderCode,
            CASE WHEN @controlIndex <= 2 OR @controlIndex > 4 THEN '2026-10-14T01:05:00' ELSE NULL END,
            CASE WHEN @controlIndex <= 2 OR @controlIndex > 4 THEN 10.00 ELSE NULL END, '2026-10-14T01:05:00');
        SET @controlIndex += 1;
    END;

    /* Historical payout records: settled March, rejected April, cancelled May. */
    DECLARE @payoutId BIGINT, @historyMonth INT, @historyStatus VARCHAR(24), @historyBookings INT,
        @historyPayments DECIMAL(14,2), @historyCommission DECIMAL(14,2), @historyNet DECIMAL(14,2),
        @historyPeriodStart DATE, @historyPeriodEnd DATE;
    DECLARE history_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT month_no, terminal_payout_status FROM @months WHERE terminal_payout_status IS NOT NULL ORDER BY month_no;
    OPEN history_cursor;
    FETCH NEXT FROM history_cursor INTO @historyMonth, @historyStatus;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @historyPeriodStart = DATEFROMPARTS(2026, @historyMonth, 1);
        SET @historyPeriodEnd = EOMONTH(@historyPeriodStart);
        SELECT @historyBookings = COUNT(*), @historyPayments = SUM(p.amount)
        FROM commerce.Bookings b
        JOIN payment.PaymentTransactions p ON p.booking_id = b.booking_id AND p.status = 'Success'
        WHERE b.booking_code LIKE CONCAT('UC46-DEMO-', @historyMonth, '-%');
        SET @historyCommission = @historyPayments * 0.10;
        SET @historyNet = @historyPayments - @historyCommission;

        INSERT payment.Payouts (
            operator_user_id, period_start, period_end, gross_revenue, commission_amount, net_amount, status,
            requested_at, reviewed_by, reviewed_at, rejection_reason, settlement_authorized_by, settlement_authorized_at,
            gateway_reference, gateway_failure_reason, gateway_attempt_count, last_gateway_attempt_at,
            payout_code, settlement_month, cut_off_at_utc, payments_received, discounts_given, refunded_amount,
            net_sales, payable_net_before_adjustments, adjustments_applied, booking_count, participant_count,
            minimum_amount_checked, bank_name, account_holder_name, account_number_masked, account_number_protected,
            cancelled_at)
        VALUES (
            @operatorId, @historyPeriodStart, @historyPeriodEnd, @historyPayments, @historyCommission, @historyNet, @historyStatus,
            DATEADD(DAY, 2, @historyPeriodEnd),
            CASE WHEN @historyStatus = 'Cancelled' THEN NULL ELSE @staffId END,
            CASE WHEN @historyStatus = 'Cancelled' THEN NULL ELSE DATEADD(DAY, 3, @historyPeriodEnd) END,
            CASE WHEN @historyStatus = 'Rejected' THEN N'UC46 demo: documentation needs correction' ELSE NULL END,
            CASE WHEN @historyStatus = 'Settled' THEN @adminId ELSE NULL END,
            CASE WHEN @historyStatus = 'Settled' THEN DATEADD(DAY, 4, @historyPeriodEnd) ELSE NULL END,
            CASE WHEN @historyStatus = 'Settled' THEN CONCAT('UC46-SETTLED-', @historyMonth) ELSE NULL END,
            NULL,
            CASE WHEN @historyStatus = 'Settled' THEN 1 ELSE 0 END,
            CASE WHEN @historyStatus = 'Settled' THEN DATEADD(DAY, 4, @historyPeriodEnd) ELSE NULL END,
            CONCAT('PO-UC46-2026', RIGHT('0' + CAST(@historyMonth AS VARCHAR(2)), 2)), @historyPeriodStart,
            DATEADD(DAY, 1, @historyPeriodEnd), @historyPayments, 0, 0, @historyPayments, @historyNet, 0,
            @historyBookings, @historyBookings, 5000000,
            N'Ngân hàng Demo', N'UC46 Demo Operator', '****4600', N'UC46-local-fixture-not-a-real-account',
            CASE WHEN @historyStatus = 'Cancelled' THEN DATEADD(DAY, 2, @historyPeriodEnd) ELSE NULL END);
        SET @payoutId = SCOPE_IDENTITY();

        IF @historyStatus = 'Settled'
        BEGIN
            INSERT payment.PayoutItems (payout_id, booking_id, paid_amount, payment_transaction_id, discount_amount, commission_rate_applied, commission_amount)
            SELECT @payoutId, b.booking_id, p.amount, p.transaction_id, 0, 10.00, p.amount * 0.10
            FROM commerce.Bookings b
            JOIN payment.PaymentTransactions p ON p.booking_id = b.booking_id AND p.status = 'Success'
            WHERE b.booking_code LIKE CONCAT('UC46-DEMO-', @historyMonth, '-%');
        END;

        FETCH NEXT FROM history_cursor INTO @historyMonth, @historyStatus;
    END;
    CLOSE history_cursor;
    DEALLOCATE history_cursor;

    COMMIT TRANSACTION;

    SELECT N'UC-46 fixtures created' AS result,
           (SELECT COUNT(*) FROM commerce.Bookings WHERE booking_code LIKE 'UC46-DEMO-%') AS bookings,
           (SELECT COUNT(*) FROM payment.PaymentTransactions p JOIN commerce.Bookings b ON b.booking_id = p.booking_id WHERE b.booking_code LIKE 'UC46-DEMO-%') AS payments,
           (SELECT COUNT(*) FROM payment.Refunds r JOIN commerce.Bookings b ON b.booking_id = r.booking_id WHERE b.booking_code LIKE 'UC46-DEMO-%') AS processed_refunds,
           (SELECT COUNT(*) FROM payment.Payouts WHERE payout_code LIKE 'PO-UC46-%') AS payout_history_rows;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
