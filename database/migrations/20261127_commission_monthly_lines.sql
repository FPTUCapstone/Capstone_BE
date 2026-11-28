-- Additive, immutable per-source calculation history. Do not backfill or reinterpret old invoices.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'commercial.TourDirectCommissionStatements',N'U') IS NULL
 THROW 51000,'Apply direct commission statement schema first.',1;
IF OBJECT_ID(N'commercial.TourCommissionCalculations',N'U') IS NULL
BEGIN
 CREATE TABLE commercial.TourCommissionCalculations(
  calculation_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_TourCommissionCalculations PRIMARY KEY,
  statement_id bigint NOT NULL,
  gross_revenue_vnd bigint NOT NULL,completed_refunds_vnd bigint NOT NULL,
  revenue_commission_vnd bigint NOT NULL,refund_commission_credit_vnd bigint NOT NULL,
  credit_brought_forward_vnd bigint NOT NULL,credit_applied_vnd bigint NOT NULL,credit_carried_forward_vnd bigint NOT NULL,
  held_booking_count int NOT NULL,ambiguous_legacy_booking_count int NOT NULL,created_at datetime2(7) NOT NULL,
  CONSTRAINT FK_TourCommissionCalculations_Statement FOREIGN KEY(statement_id) REFERENCES commercial.TourDirectCommissionStatements(statement_id),
  CONSTRAINT CK_TourCommissionCalculations_Nonnegative CHECK(gross_revenue_vnd>=0 AND completed_refunds_vnd>=0 AND revenue_commission_vnd>=0
   AND refund_commission_credit_vnd>=0 AND credit_brought_forward_vnd>=0 AND credit_applied_vnd>=0 AND credit_carried_forward_vnd>=0
   AND held_booking_count>=0 AND ambiguous_legacy_booking_count>=0),
  CONSTRAINT CK_TourCommissionCalculations_Credit CHECK(credit_applied_vnd<=revenue_commission_vnd AND
   credit_applied_vnd+credit_carried_forward_vnd=refund_commission_credit_vnd+credit_brought_forward_vnd)
 );
 CREATE UNIQUE INDEX UX_TourCommissionCalculations_Statement ON commercial.TourCommissionCalculations(statement_id);
END;
IF OBJECT_ID(N'commercial.TourCommissionLedgerEntries',N'U') IS NULL
BEGIN
 CREATE TABLE commercial.TourCommissionLedgerEntries(
  entry_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_TourCommissionLedgerEntries PRIMARY KEY,
  statement_id bigint NOT NULL,booking_id bigint NOT NULL,source_type varchar(24) NOT NULL,source_id bigint NOT NULL,
  payment_transaction_id bigint NOT NULL,base_amount_vnd bigint NOT NULL,commission_rate decimal(5,4) NOT NULL,
  commission_amount_vnd bigint NOT NULL,source_occurred_at datetime2(7) NOT NULL,created_at datetime2(7) NOT NULL,
  CONSTRAINT FK_TourCommissionLedgerEntries_Statement FOREIGN KEY(statement_id) REFERENCES commercial.TourDirectCommissionStatements(statement_id),
  CONSTRAINT FK_TourCommissionLedgerEntries_Booking FOREIGN KEY(booking_id) REFERENCES commerce.Bookings(booking_id),
  CONSTRAINT FK_TourCommissionLedgerEntries_Payment FOREIGN KEY(payment_transaction_id) REFERENCES payment.PaymentTransactions(transaction_id),
  CONSTRAINT CK_TourCommissionLedgerEntries_Source CHECK(source_type IN('BookingRevenue','DirectRefundCredit','LegacyRefundCredit') AND source_id>0),
  CONSTRAINT CK_TourCommissionLedgerEntries_Rate CHECK(commission_rate>0 AND commission_rate<=1),
  CONSTRAINT CK_TourCommissionLedgerEntries_Sign CHECK((source_type='BookingRevenue' AND base_amount_vnd>0 AND commission_amount_vnd>=0)
   OR(source_type<>'BookingRevenue' AND base_amount_vnd<0 AND commission_amount_vnd<=0))
 );
 CREATE UNIQUE INDEX UX_TourCommissionLedgerEntries_Source ON commercial.TourCommissionLedgerEntries(source_type,source_id);
 CREATE INDEX IX_TourCommissionLedgerEntries_Booking ON commercial.TourCommissionLedgerEntries(booking_id,source_type);
END;
COMMIT;
