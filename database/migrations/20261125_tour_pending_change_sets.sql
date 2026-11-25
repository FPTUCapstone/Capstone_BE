/*
    TM-82 / UC-36: draft versions for editing an Approved/Published tour package (BR-103).
    Additive/idempotent SQL Server migration. The live commerce.Tours row is never edited in
    place while a draft version is open; see specs/TM-82-spec.md §4.1/§8.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'TM-82 requires commerce.Tours.', 1;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'TM-82 requires dbo.Users.', 1;
    IF OBJECT_ID(N'commerce.CancellationPolicyTemplates', N'U') IS NULL
        THROW 51000, 'TM-82 requires commerce.CancellationPolicyTemplates.', 1;

    IF OBJECT_ID(N'commerce.TourPendingChangeSets', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.TourPendingChangeSets (
            tour_pending_change_set_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourPendingChangeSets PRIMARY KEY,
            tour_id BIGINT NOT NULL,
            submitted_by_user_id BIGINT NOT NULL,
            status VARCHAR(13) NOT NULL,
            category_id INT NULL,
            title NVARCHAR(200) NOT NULL,
            description NVARCHAR(MAX) NOT NULL,
            base_price DECIMAL(12,2) NOT NULL,
            duration_days INT NOT NULL,
            inclusions NVARCHAR(MAX) NULL,
            exclusions NVARCHAR(MAX) NULL,
            highlights NVARCHAR(MAX) NULL,
            traveller_requirements NVARCHAR(MAX) NULL,
            time_zone_id NVARCHAR(100) NOT NULL,
            destination_summary NVARCHAR(300) NOT NULL,
            cancellation_policy_template_id BIGINT NOT NULL,
            destination_ids_json NVARCHAR(MAX) NOT NULL,
            itinerary_items_json NVARCHAR(MAX) NOT NULL,
            schedules_json NVARCHAR(MAX) NOT NULL,
            created_at DATETIME2 NOT NULL CONSTRAINT DF_TourPendingChangeSets_CreatedAt DEFAULT SYSUTCDATETIME(),
            updated_at DATETIME2 NOT NULL CONSTRAINT DF_TourPendingChangeSets_UpdatedAt DEFAULT SYSUTCDATETIME(),
            submitted_at DATETIME2 NULL,
            reviewed_by_user_id BIGINT NULL,
            reviewed_at DATETIME2 NULL,
            rejection_reason NVARCHAR(500) NULL,
            CONSTRAINT FK_TourPendingChangeSets_Tour FOREIGN KEY (tour_id)
                REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
            CONSTRAINT FK_TourPendingChangeSets_SubmittedBy FOREIGN KEY (submitted_by_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_TourPendingChangeSets_ReviewedBy FOREIGN KEY (reviewed_by_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_TourPendingChangeSets_CancellationPolicyTemplate FOREIGN KEY (cancellation_policy_template_id)
                REFERENCES commerce.CancellationPolicyTemplates(cancellation_policy_template_id),
            CONSTRAINT CK_TourPendingChangeSets_Status
                CHECK (status IN ('Draft','PendingReview','Applied','Rejected'))
        );
        CREATE INDEX IX_TourPendingChangeSets_Tour ON commerce.TourPendingChangeSets(tour_id);
    END;

    /* Spec §3 point G6/§8: at most one open (Draft/PendingReview/Rejected) change set per tour,
       enforced at the database level so a race between two create calls cannot both succeed. */
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'UX_TourPendingChangeSets_OpenPerTour'
          AND object_id = OBJECT_ID(N'commerce.TourPendingChangeSets'))
        CREATE UNIQUE INDEX UX_TourPendingChangeSets_OpenPerTour
            ON commerce.TourPendingChangeSets(tour_id)
            WHERE status IN ('Draft','PendingReview','Rejected');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
