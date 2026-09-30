/*
    Development/test seed for UC-51 Reject Tour Operator Application.

    This script is intentionally not invoked by database/apply-schema.sh.
    It creates no usable credentials and must not be applied to production.
    Re-running it resets only the three @tripmate.local fixtures below.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;

MERGE dbo.Users AS target
USING (VALUES
    (N'uc51.pending@tripmate.local', N'UC-51 Pending Operator',  'PendingApproval'),
    (N'uc51.pending-500@tripmate.local', N'UC-51 Boundary Operator', 'PendingApproval'),
    (N'uc51.pending-trim@tripmate.local', N'UC-51 Trim Operator', 'PendingApproval'),
    (N'uc51.pending-extra@tripmate.local', N'UC-51 Extra Operator', 'PendingApproval'),
    (N'uc51.approved@tripmate.local', N'UC-51 Approved Operator', 'Active'),
    (N'uc51.rejected@tripmate.local', N'UC-51 Rejected Operator', 'Rejected')
) AS source(email, full_name, status)
ON target.email = source.email
WHEN MATCHED THEN UPDATE SET
    role = 'TourOperator',
    full_name = source.full_name,
    status = source.status,
    password_hash = NULL,
    updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (role, email, password_hash, full_name, status, email_verified_at, created_at, updated_at)
VALUES
    ('TourOperator', source.email, NULL, source.full_name, source.status,
     SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());

DECLARE @PendingUserId BIGINT =
    (SELECT user_id FROM dbo.Users WHERE email = N'uc51.pending@tripmate.local');
DECLARE @ApprovedUserId BIGINT =
    (SELECT user_id FROM dbo.Users WHERE email = N'uc51.approved@tripmate.local');
DECLARE @PendingBoundaryUserId BIGINT =
    (SELECT user_id FROM dbo.Users WHERE email = N'uc51.pending-500@tripmate.local');
DECLARE @PendingTrimUserId BIGINT =
    (SELECT user_id FROM dbo.Users WHERE email = N'uc51.pending-trim@tripmate.local');
DECLARE @PendingExtraUserId BIGINT =
    (SELECT user_id FROM dbo.Users WHERE email = N'uc51.pending-extra@tripmate.local');
DECLARE @RejectedUserId BIGINT =
    (SELECT user_id FROM dbo.Users WHERE email = N'uc51.rejected@tripmate.local');

MERGE dbo.OperatorProfiles AS target
USING (VALUES
    (@PendingUserId,  N'UC-51 Pending Travel',  N'UC51-TAX-PENDING',  N'UC51-LIC-PENDING',  'PendingApproval', CAST(NULL AS NVARCHAR(500))),
    (@PendingBoundaryUserId, N'UC-51 Boundary Travel', N'UC51-TAX-BOUNDARY', N'UC51-LIC-BOUNDARY', 'PendingApproval', CAST(NULL AS NVARCHAR(500))),
    (@PendingTrimUserId, N'UC-51 Trim Travel', N'UC51-TAX-TRIM', N'UC51-LIC-TRIM', 'PendingApproval', CAST(NULL AS NVARCHAR(500))),
    (@PendingExtraUserId, N'UC-51 Extra Travel', N'UC51-TAX-EXTRA', N'UC51-LIC-EXTRA', 'PendingApproval', CAST(NULL AS NVARCHAR(500))),
    (@ApprovedUserId, N'UC-51 Approved Travel', N'UC51-TAX-APPROVED', N'UC51-LIC-APPROVED', 'Approved',        CAST(NULL AS NVARCHAR(500))),
    (@RejectedUserId, N'UC-51 Rejected Travel', N'UC51-TAX-REJECTED', N'UC51-LIC-REJECTED', 'Rejected',        N'Existing rejection fixture for conflict testing.')
) AS source(user_id, company_name, tax_code, business_license_no, approval_status, rejection_reason)
ON target.user_id = source.user_id
WHEN MATCHED THEN UPDATE SET
    company_name = source.company_name,
    tax_code = source.tax_code,
    business_license_no = source.business_license_no,
    approval_status = source.approval_status,
    rejection_reason = source.rejection_reason,
    reviewed_by = NULL,
    reviewed_at = CASE WHEN source.approval_status = 'Rejected' THEN SYSUTCDATETIME() ELSE NULL END,
    updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (user_id, company_name, tax_code, business_license_no, commission_rate,
     approval_status, rejection_reason, reviewed_by, reviewed_at, created_at, updated_at)
VALUES
    (source.user_id, source.company_name, source.tax_code, source.business_license_no, 10.00,
     source.approval_status, source.rejection_reason, NULL,
     CASE WHEN source.approval_status = 'Rejected' THEN SYSUTCDATETIME() ELSE NULL END,
     SYSUTCDATETIME(), SYSUTCDATETIME());

DELETE FROM dbo.OperatorDocuments
WHERE operator_user_id IN (
    @PendingUserId,
    @PendingBoundaryUserId,
    @PendingTrimUserId,
    @PendingExtraUserId,
    @ApprovedUserId,
    @RejectedUserId
);

INSERT INTO dbo.OperatorDocuments
    (operator_user_id, document_type, file_url, status, uploaded_at)
VALUES
    (@PendingUserId,  'BusinessLicense', N'https://example.invalid/uc51/pending-license.pdf',  'Submitted', SYSUTCDATETIME()),
    (@PendingBoundaryUserId, 'BusinessLicense', N'https://example.invalid/uc51/boundary-license.pdf', 'Submitted', SYSUTCDATETIME()),
    (@PendingTrimUserId, 'BusinessLicense', N'https://example.invalid/uc51/trim-license.pdf', 'Submitted', SYSUTCDATETIME()),
    (@PendingExtraUserId, 'BusinessLicense', N'https://example.invalid/uc51/extra-license.pdf', 'Submitted', SYSUTCDATETIME()),
    (@ApprovedUserId, 'BusinessLicense', N'https://example.invalid/uc51/approved-license.pdf', 'Approved',  SYSUTCDATETIME()),
    (@RejectedUserId, 'BusinessLicense', N'https://example.invalid/uc51/rejected-license.pdf', 'Rejected',  SYSUTCDATETIME());

COMMIT TRANSACTION;

SELECT u.user_id, u.email, u.status AS account_status, op.approval_status,
       op.rejection_reason
FROM dbo.Users AS u
INNER JOIN dbo.OperatorProfiles AS op ON op.user_id = u.user_id
WHERE u.email IN (
    N'uc51.pending@tripmate.local',
    N'uc51.pending-500@tripmate.local',
    N'uc51.pending-trim@tripmate.local',
    N'uc51.pending-extra@tripmate.local',
    N'uc51.approved@tripmate.local',
    N'uc51.rejected@tripmate.local'
)
ORDER BY u.email;
