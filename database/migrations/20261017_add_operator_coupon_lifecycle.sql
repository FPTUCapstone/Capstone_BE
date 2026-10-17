/* UC-38/UC-39: persist the operator-facing coupon name and modification timestamp. */
IF COL_LENGTH('commerce.Vouchers', 'name') IS NULL
BEGIN
    ALTER TABLE commerce.Vouchers ADD name NVARCHAR(120) NULL;
    UPDATE commerce.Vouchers SET name = code WHERE name IS NULL;
    ALTER TABLE commerce.Vouchers ALTER COLUMN name NVARCHAR(120) NOT NULL;
END;
GO

IF COL_LENGTH('commerce.Vouchers', 'updated_at') IS NULL
BEGIN
    ALTER TABLE commerce.Vouchers ADD updated_at DATETIME2 NULL;
    UPDATE commerce.Vouchers SET updated_at = created_at WHERE updated_at IS NULL;
    ALTER TABLE commerce.Vouchers ALTER COLUMN updated_at DATETIME2 NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID('commerce.Vouchers')
      AND parent_column_id = COLUMNPROPERTY(OBJECT_ID('commerce.Vouchers'), 'updated_at', 'ColumnId'))
BEGIN
    ALTER TABLE commerce.Vouchers ADD CONSTRAINT DF_Vouchers_UpdatedAt DEFAULT SYSUTCDATETIME() FOR updated_at;
END;
GO

