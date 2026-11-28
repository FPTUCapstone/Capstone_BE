SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'commercial.TourDirectCommissionStatements',N'U') IS NULL
 THROW 51000,'Apply direct commission statement schema first.',1;
IF OBJECT_ID(N'commercial.TourCommissionPaymentAttempts',N'U') IS NULL
BEGIN
 CREATE TABLE commercial.TourCommissionPaymentAttempts(
  payment_id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_TourCommissionPaymentAttempts PRIMARY KEY,
  statement_id bigint NOT NULL,order_code bigint NOT NULL,gateway varchar(20) NOT NULL,amount_vnd bigint NOT NULL,
  status varchar(20) NOT NULL,payment_link_id varchar(100) NULL,checkout_url varchar(2000) NULL,gateway_reference varchar(150) NULL,
  created_at datetime2(7) NOT NULL,updated_at datetime2(7) NOT NULL,expires_at datetime2(7) NOT NULL,row_version rowversion NOT NULL,
  CONSTRAINT FK_TourCommissionPaymentAttempts_Statement FOREIGN KEY(statement_id) REFERENCES commercial.TourDirectCommissionStatements(statement_id),
  CONSTRAINT CK_TourCommissionPaymentAttempts_Order CHECK(order_code>=8000000000000000 AND order_code<8001000000000000),
  CONSTRAINT CK_TourCommissionPaymentAttempts_Amount CHECK(amount_vnd>0),
  CONSTRAINT CK_TourCommissionPaymentAttempts_Gateway CHECK(gateway IN ('Mock','PayOS')),
  CONSTRAINT CK_TourCommissionPaymentAttempts_Status CHECK(status IN ('Creating','Pending','Success','Cancelled'))
 );
 CREATE UNIQUE INDEX UX_TourCommissionPaymentAttempts_Order ON commercial.TourCommissionPaymentAttempts(order_code);
 CREATE UNIQUE INDEX UX_TourCommissionPaymentAttempts_Active ON commercial.TourCommissionPaymentAttempts(statement_id) WHERE status IN ('Creating','Pending');
 CREATE UNIQUE INDEX UX_TourCommissionPaymentAttempts_Reference ON commercial.TourCommissionPaymentAttempts(gateway,gateway_reference) WHERE gateway_reference IS NOT NULL;
END;
COMMIT;
