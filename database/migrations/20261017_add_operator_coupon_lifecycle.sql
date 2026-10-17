/* UC-38/UC-39: persist the operator-facing coupon name and modification timestamp.
   Each step is its own batch: a statement that uses a column cannot share a batch with the
   ALTER TABLE that adds it (SQL Server compiles the whole batch first and fails with
   "Invalid column name"). Every step can be re-run and resumes from a partly applied state. */
IF COL_LENGTH('commerce.Vouchers', 'name') IS NULL
BEGIN
    ALTER TABLE commerce.Vouchers ADD name NVARCHAR(120) NULL;
END;
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('commerce.Vouchers') AND name = 'name' AND is_nullable = 1)
BEGIN
    UPDATE commerce.Vouchers SET name = code WHERE name IS NULL;
    ALTER TABLE commerce.Vouchers ALTER COLUMN name NVARCHAR(120) NOT NULL;
END;
GO

IF COL_LENGTH('commerce.Vouchers', 'updated_at') IS NULL
BEGIN
    ALTER TABLE commerce.Vouchers ADD updated_at DATETIME2 NULL;
END;
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('commerce.Vouchers') AND name = 'updated_at' AND is_nullable = 1)
BEGIN
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
