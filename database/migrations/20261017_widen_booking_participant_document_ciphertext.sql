/*
    UC-27 / BR-33
    ASP.NET Core Data Protection serializes an encrypted document number to a value larger than
    the legacy 50-character clear-text column. Keep the upgrade additive and idempotent while
    aligning it with the fresh schema's NVARCHAR(512) definition.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF OBJECT_ID(N'commerce.BookingParticipants', N'U') IS NULL
    THROW 51000, N'Expected table commerce.BookingParticipants was not found.', 1;

IF COL_LENGTH(N'commerce.BookingParticipants', N'id_document_number') IS NULL
    THROW 51000, N'Expected column commerce.BookingParticipants.id_document_number was not found.', 1;

IF EXISTS
(
    SELECT 1
    FROM sys.columns AS column_definition
    INNER JOIN sys.types AS type_definition ON type_definition.user_type_id = column_definition.user_type_id
    WHERE column_definition.object_id = OBJECT_ID(N'commerce.BookingParticipants')
      AND column_definition.name = N'id_document_number'
      AND type_definition.name = N'nvarchar'
      AND column_definition.max_length <> -1
      AND column_definition.max_length < 1024
)
BEGIN
    ALTER TABLE commerce.BookingParticipants
        ALTER COLUMN id_document_number NVARCHAR(512) NULL;
END;

COMMIT TRANSACTION;
