/* UC-65 - Confirm Payout Settlement: bank catalogue, payout beneficiary BIN, and the durable
   settlement work item the payout worker consumes.
   - payment.Banks: standard NAPAS/VietQR catalogue, seeded with the team-verified rows below.
   - payment.Payouts.bank_bin: resolved at authorization time; existing rows stay NULL (no backfill).
   - payment.PayoutSettlementWorkItems: exactly one per payout (unique payout_id).
   The script is ASCII-only: accented characters are written as NCHAR(code point) so the result does
   not depend on the code page sqlcmd uses to read the file. It is idempotent. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'payment.Banks', N'U') IS NULL
BEGIN
    CREATE TABLE payment.Banks (
        bank_bin       CHAR(6)       NOT NULL CONSTRAINT PK_Banks PRIMARY KEY,
        short_name     NVARCHAR(50)  NOT NULL,
        official_name  NVARCHAR(200) NOT NULL,
        created_at     DATETIME2     NOT NULL CONSTRAINT DF_Banks_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_Banks_Bin CHECK (bank_bin NOT LIKE '%[^0-9]%' AND LEN(bank_bin) = 6),
        CONSTRAINT UX_Banks_ShortName UNIQUE (short_name),
        CONSTRAINT UX_Banks_OfficialName UNIQUE (official_name)
    );
END;

/* Catalogue rows (BIN, short name, official name). Re-running inserts only missing BINs. */
-- 970436 Vietcombank - Ngan hang TMCP Ngoai Thuong Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970436')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970436', N'Vietcombank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Ngo' + NCHAR(7841) + N'i Th' + NCHAR(432) + NCHAR(417) + N'ng Vi' + NCHAR(7879) + N't Nam');

-- 970415 VietinBank - Ngan hang TMCP Cong thuong Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970415')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970415', N'VietinBank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP C' + NCHAR(244) + N'ng th' + NCHAR(432) + NCHAR(417) + N'ng Vi' + NCHAR(7879) + N't Nam');

-- 970418 BIDV - Ngan hang TMCP Dau tu va Phat trien Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970418')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970418', N'BIDV', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP ' + NCHAR(272) + NCHAR(7847) + N'u t' + NCHAR(432) + N' v' + NCHAR(224) + N' Ph' + NCHAR(225) + N't tri' + NCHAR(7875) + N'n Vi' + NCHAR(7879) + N't Nam');

-- 970405 Agribank - Ngan hang Nong nghiep va Phat trien Nong thon Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970405')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970405', N'Agribank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng N' + NCHAR(244) + N'ng nghi' + NCHAR(7879) + N'p v' + NCHAR(224) + N' Ph' + NCHAR(225) + N't tri' + NCHAR(7875) + N'n N' + NCHAR(244) + N'ng th' + NCHAR(244) + N'n Vi' + NCHAR(7879) + N't Nam');

-- 970407 Techcombank - Ngan hang TMCP Ky thuong Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970407')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970407', N'Techcombank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP K' + NCHAR(7929) + N' th' + NCHAR(432) + NCHAR(417) + N'ng Vi' + NCHAR(7879) + N't Nam');

-- 970422 MBBank - Ngan hang TMCP Quan doi
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970422')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970422', N'MBBank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Qu' + NCHAR(226) + N'n ' + NCHAR(273) + NCHAR(7897) + N'i');

-- 970416 ACB - Ngan hang TMCP A Chau
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970416')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970416', N'ACB', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP ' + NCHAR(193) + N' Ch' + NCHAR(226) + N'u');

-- 970432 VPBank - Ngan hang TMCP Viet Nam Thinh Vuong
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970432')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970432', N'VPBank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Vi' + NCHAR(7879) + N't Nam Th' + NCHAR(7883) + N'nh V' + NCHAR(432) + NCHAR(7907) + N'ng');

-- 970423 TPBank - Ngan hang TMCP Tien Phong
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970423')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970423', N'TPBank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Ti' + NCHAR(234) + N'n Phong');

-- 970403 Sacombank - Ngan hang TMCP Sai Gon Thuong Tin
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970403')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970403', N'Sacombank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP S' + NCHAR(224) + N'i G' + NCHAR(242) + N'n Th' + NCHAR(432) + NCHAR(417) + N'ng T' + NCHAR(237) + N'n');

-- 970437 HDBank - Ngan hang TMCP Phat trien Thanh pho Ho Chi Minh
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970437')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970437', N'HDBank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Ph' + NCHAR(225) + N't tri' + NCHAR(7875) + N'n Th' + NCHAR(224) + N'nh ph' + NCHAR(7889) + N' H' + NCHAR(7891) + N' Ch' + NCHAR(237) + N' Minh');

-- 970441 VIB - Ngan hang TMCP Quoc te Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970441')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970441', N'VIB', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Qu' + NCHAR(7889) + N'c t' + NCHAR(7871) + N' Vi' + NCHAR(7879) + N't Nam');

-- 970443 SHB - Ngan hang TMCP Sai Gon - Ha Noi
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970443')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970443', N'SHB', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP S' + NCHAR(224) + N'i G' + NCHAR(242) + N'n - H' + NCHAR(224) + N' N' + NCHAR(7897) + N'i');

-- 970431 Eximbank - Ngan hang TMCP Xuat Nhap khau Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970431')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970431', N'Eximbank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Xu' + NCHAR(7845) + N't Nh' + NCHAR(7853) + N'p kh' + NCHAR(7849) + N'u Vi' + NCHAR(7879) + N't Nam');

-- 970426 MSB - Ngan hang TMCP Hang Hai Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970426')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970426', N'MSB', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP H' + NCHAR(224) + N'ng H' + NCHAR(7843) + N'i Vi' + NCHAR(7879) + N't Nam');

-- 970448 OCB - Ngan hang TMCP Phuong Dong
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970448')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970448', N'OCB', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP Ph' + NCHAR(432) + NCHAR(417) + N'ng ' + NCHAR(272) + NCHAR(244) + N'ng');

-- 970449 LPBank - Ngan hang TMCP Loc Phat Viet Nam
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970449')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970449', N'LPBank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP L' + NCHAR(7897) + N'c Ph' + NCHAR(225) + N't Vi' + NCHAR(7879) + N't Nam');

-- 970454 VietCapitalBank - Ngan hang TMCP Ban Viet
IF NOT EXISTS (SELECT 1 FROM payment.Banks WHERE bank_bin = '970454')
    INSERT INTO payment.Banks (bank_bin, short_name, official_name)
    VALUES ('970454', N'VietCapitalBank', N'Ng' + NCHAR(226) + N'n h' + NCHAR(224) + N'ng TMCP B' + NCHAR(7843) + N'n Vi' + NCHAR(7879) + N't');

IF COL_LENGTH(N'payment.Payouts', N'bank_bin') IS NULL
    ALTER TABLE payment.Payouts ADD bank_bin CHAR(6) NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'payment.Payouts') AND name = N'CK_Payouts_BankBin')
    EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_BankBin CHECK (
        bank_bin IS NULL OR (bank_bin NOT LIKE ''%[^0-9]%'' AND LEN(bank_bin) = 6));');

IF OBJECT_ID(N'payment.PayoutSettlementWorkItems', N'U') IS NULL
BEGIN
    CREATE TABLE payment.PayoutSettlementWorkItems (
        work_item_id       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutSettlementWorkItems PRIMARY KEY,
        payout_id          BIGINT           NOT NULL,
        state              VARCHAR(24)      NOT NULL,
        attempt_count      INT              NOT NULL CONSTRAINT DF_PayoutSettlementWorkItems_Attempts DEFAULT 0,
        next_attempt_at    DATETIME2        NULL,
        lease_token        UNIQUEIDENTIFIER NULL,
        lease_expires_at   DATETIME2        NULL,
        gateway_reference  NVARCHAR(200)    NULL,
        submitted_at       DATETIME2        NULL,
        poll_deadline_at   DATETIME2        NULL,
        poll_count         INT              NOT NULL CONSTRAINT DF_PayoutSettlementWorkItems_Polls DEFAULT 0,
        last_polled_at     DATETIME2        NULL,
        last_error_code    VARCHAR(100)     NULL,
        created_at         DATETIME2        NOT NULL,
        completed_at       DATETIME2        NULL,
        row_version        ROWVERSION       NOT NULL,
        CONSTRAINT CK_PayoutSettlementWorkItems_State
            CHECK (state IN ('Pending','Submitting','Submitted','Completed','ManualReconciliation')),
        CONSTRAINT CK_PayoutSettlementWorkItems_Counts CHECK (attempt_count >= 0 AND poll_count >= 0),
        CONSTRAINT FK_PayoutSettlementWorkItems_Payout FOREIGN KEY (payout_id)
            REFERENCES payment.Payouts(payout_id),
        CONSTRAINT UX_PayoutSettlementWorkItems_Payout UNIQUE (payout_id)
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'payment.PayoutSettlementWorkItems') AND name = N'IX_PayoutSettlementWorkItems_Due')
    EXEC(N'CREATE INDEX IX_PayoutSettlementWorkItems_Due
        ON payment.PayoutSettlementWorkItems(state, next_attempt_at);');

COMMIT TRANSACTION;
