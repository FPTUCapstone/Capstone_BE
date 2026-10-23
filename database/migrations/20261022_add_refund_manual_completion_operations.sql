/* UC-42 — durable idempotency for administrator-recorded manual settlements. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'payment.RefundManualCompletionOperations', N'U') IS NULL
BEGIN
    CREATE TABLE payment.RefundManualCompletionOperations (
        operation_id         BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_RefundManualCompletionOperations PRIMARY KEY,
        refund_id            BIGINT NOT NULL,
        actor_user_id        BIGINT NOT NULL,
        idempotency_key      UNIQUEIDENTIFIER NOT NULL,
        request_hash         CHAR(64) NOT NULL,
        gateway_reference    NVARCHAR(150) NOT NULL,
        completed_at         DATETIME2 NOT NULL,
        CONSTRAINT UX_RefundManualCompletionOperations_ActorKey UNIQUE (actor_user_id, idempotency_key),
        CONSTRAINT UX_RefundManualCompletionOperations_Refund UNIQUE (refund_id),
        CONSTRAINT FK_RefundManualCompletionOperations_Refund FOREIGN KEY (refund_id)
            REFERENCES payment.Refunds(refund_id),
        CONSTRAINT FK_RefundManualCompletionOperations_Actor FOREIGN KEY (actor_user_id)
            REFERENCES dbo.Users(user_id)
    );
END;

COMMIT TRANSACTION;
