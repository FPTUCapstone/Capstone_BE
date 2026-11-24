IF OBJECT_ID(N'commerce.TourApprovalNoticeOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE commerce.TourApprovalNoticeOutbox (
        tour_id BIGINT NOT NULL,
        operator_user_id BIGINT NOT NULL,
        tour_title NVARCHAR(200) NOT NULL,
        approved_at_utc DATETIME2 NOT NULL,
        next_attempt_at_utc DATETIME2 NOT NULL,
        CONSTRAINT PK_TourApprovalNoticeOutbox PRIMARY KEY (tour_id)
    );

    CREATE INDEX IX_TourApprovalNoticeOutbox_Due
        ON commerce.TourApprovalNoticeOutbox (next_attempt_at_utc, tour_id);
END;
GO
