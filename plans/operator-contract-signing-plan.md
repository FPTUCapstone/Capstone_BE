# Operator contract signing — implementation plan
Status: Authorized for implementation on 24/11/2026, with email OTP and recorded legacy exemptions. Created: 23/11/2026.
Branch: fix/linhnv-operator-sign-up.
Canonical specification: ../specs/operator-contract-signing-spec.md.

## Tiến độ ngày 24/11/2026
Người dùng đã yêu cầu triển khai TO xem hợp đồng và vẽ chữ ký sau khi test Admin template thành công. Đang thực hiện capture độc lập Web/Mobile và proof PDF; hai lựa chọn OTP/legacy đã được hỏi, công việc phụ thuộc đang chờ câu trả lời. Chưa sửa approval, trạng thái tài khoản hoặc áp dụng migration ký.
Đã review read-only approval/auth/schema; ghi các phát hiện tại spec mục 13. Proof PDF dùng project tạm `.tmp/signature-pdf-probe`, không phải signed artifact production.

## 0. Decisions and baseline
Approve specification section 12 before implementation: signature method, official template, signer authority, OTP policy if selected, legacy accounts, retention and template publishing.
Preserve all existing UI changes. Record current git status and relevant auth/approval test baselines. Do not treat this document as authorization to implement, deploy or send real contracts.

## 1. Technical feasibility
Implement the prerequisite admin-contract-template-management plan first. Reuse its versioned template entity, private storage, renderer and signature-anchor output; do not duplicate these tables/adapters in the signing implementation. Keep the waiting-signature account gate disabled until signing clients are ready.
Verify private document storage, authorized streaming, SHA-256 integrity and a renderer capable of producing a durable signed artifact from exactly the accepted template/snapshot.
For drawn signatures, verify capture on Web and Mobile, accessible alternatives and file validation. For OTP, verify delivery, challenge binding and rate limits. For an external provider, verify webhook authenticity and duplicate callbacks.
Completion: demonstrate artifact integrity and failure recovery; do not claim a mock signature verifies identity. Select dependencies only after this check.

## 2. Domain and SQL foundation
Proposed files:
- src/TripMate.Domain/Entities/OperatorContract.cs
- src/TripMate.Domain/Entities/OperatorContractTemplate.cs
- src/TripMate.Domain/Entities/ContractSigningOperation.cs
- src/TripMate.Domain/Enums/AccountStatus.cs and contract-status enum.
- Infrastructure/Persistence/Configurations: UserConfiguration and three new entity configurations.
- ApplicationDbContext and IApplicationDbContext registrations.
- database/migrations/<approved-date>_operator_contract_signing.sql.

Write failing lifecycle, mapping and SQL migration tests first. Add immutable template/contract snapshots, row versions and uniqueness for issuance/signing operations. Update account CHECK constraints. Use idempotent SQL, not EF migrations.
Test fresh DB, repeated application, existing data and the approved legacy policy. Back up local DB before applying; verify row counts and migration ledger.
Completion: unsigned accounts cannot become Active; no existing account is silently downgraded.

## 3. Staff approval
Modify Features/Admin/TourOperatorApplications/Approve/ApproveOperatorApplicationCommandHandler.cs, response DTO and messages.
Failing tests: Staff approval creates one contract, profile Approved and account PendingContractSignature; missing template rolls back; duplicate/concurrent approval cannot create duplicate contracts.
Keep template selection/snapshot and approval atomic. Do not perform external network operations inside the transaction.
Completion: approval feedback says awaiting signature rather than activated.

## 4. Contract API slices
Create Features/Operator/Contracts/{GetCurrent,Download,Sign}: query/command, validator, handler; expose thin V1 OperatorContractsController.
If OTP is approved, add a dedicated signing-challenge slice and verification port. Reuse existing storage/email/audit infrastructure where compatible.
Execute separate units: read metadata; authorized document download; challenge if applicable; signature validation; staged artifact; conditional finalization; cleanup.
Tests: owner/Staff permissions, other-user denial, wrong version/hash, invalid file, unavailable storage/provider, duplicate key, concurrent signing and lost response.
Completion: signed artifact is durable before activation commits; retries return the existing outcome.

## 5. Authentication and workspace enforcement
Inspect actual Login, Google, Web login, refresh/session handlers and response mappers. Update AuthResponseDto/WebAuthResponseDto, operator application DTOs and account/session checks.
Inventory every operator business guard currently checking only ApprovalStatus.Approved. Require the persisted account/contract condition, including old-token and direct-API requests. Cover account lock/unlock, effective status and provisioning paths.
Use the existing session-version/refresh mechanism after activation.
Tests: pending accounts can authenticate but cannot access business APIs; stale tokens, Locked/Inactive accounts, unresolved states and legacy accounts follow the specification.
Completion: no route or client-side redirect can bypass the backend gate.

## 6. Web
Proposed feature: src/features/operator/contracts/{components,services,types,resources}.
Add a contract page under the existing partner route structure and authorized BFF routes for contract APIs.
Modify operator/access/{PartnerRouteGuard,partnerRouteDecision}, operator/application/OperatorApplicationStatusPage, auth/session, src/lib/routes.ts, profile entry and Staff approval feedback.
Implement in units: viewer; signer confirmation/capture; submit and retry; session refresh; read-only signed view.
Behavior tests: awaiting-signature routing, direct links, correct version, invalid signature, 409/retry, duplicate click and successful session refresh.
Visually check long documents, narrow screens and keyboard access. Do not add automated tests solely for positioning/CSS, following the user's preference.

## 7. Mobile
Add contract Domain entities/repository/use cases; Data DTO/source/repository; OperatorContractCubit and page.
Modify auth response/session mapping, operator application page, app/router/{app_routes,app_router,route_guards}, core/di and profile entry.
Keep application approval and onboarding state separate. Unknown states fail closed.
Tests: mapping, login/resume/deep links, file validation, signing failure/retry, concurrent cross-device signing, session refresh and ISO payloads versus dd/MM/yyyy display.
Add native viewer/share/signature dependencies only after feasibility and platform compatibility checks.
Completion: real backend state controls activation; no fake success or local status override.

## 8. End-to-end and delivery
Test Staff approval -> awaiting signature -> TO login -> contract -> signing -> Active on Web and Android, plus viewing the signed document afterwards.
Cover upload failure before commit, duplicate/concurrent requests, changed/voided contract, another device signing first, direct business API denial and the chosen legacy policy.
Run relevant BE build/unit/integration checks; FE lint/typecheck/build and behavior tests; Mobile pub get/format/analyze/tests and APK build when native/runtime changes require it. Report skips and baseline failures honestly.
Perform independent specification-compliance and code-quality review according to AGENTS.
Update SRS UC02/UC03/UC49 and BR07 wording; configuration examples use placeholders only.
Deploy migration -> compatible BE -> clients -> enable the gate. Rollback must retain signed evidence. No production DB changes, commit, push, PR or deployment without separate authorization.

## Execution discipline
Each numbered workstream is split into the units listed above; write the failing behavioral test, implement, verify and review before proceeding.
Inspect proposed paths against the actual repository before adding files. Do not create empty layers. Stop dependent work when signing method, template content or authority rules remain undecided.

## Verification record 24/11/2026
- BE unit checks: Domain/auth foundation 10 passed; approval/workflow and existing approval regression group 18 passed; OTP adapters 18 passed; document creation/image/stamping group 20 passed.
- SQL scratch database: migration and OTP update-lock checks 5 passed, no skips. API persisted workspace authorization checks 2 passed.
- BE Release build succeeded with zero warnings/errors. C# formatting applied to changed files.
- Local database backed up with COPY_ONLY/CHECKSUM and verified before migration: `../backups/TripMateDb-before-operator-signing-20261124-ee2b9bbe.bak`. Signing migration recorded Applied; 66 preexisting users preserved; 6 legacy exemptions recorded. An explicitly marked synthetic E2E operator was later added by the test harness.
- Actual local DB + private storage + LibreOffice Docker + PNG inspection + PDFsharp stamping proof passed: Staff approval, PendingContractSignature, prepared 67203-byte PDF, signed 72104-byte PDF, Active, same-operation replay once. PDF last page visually checked; content retained and signature placed in reserved space. OTP was capture-only inside a temporary harness, not a product bypass and not real email delivery.
- Web: 191 initial integration tests passed; final contract/application-edge group 42 passed; scoped lint/typecheck passed; Next 16.4.5 production build passed. Local FE 3002 targets BE 5022.
- Mobile: pdfx 2.11.0 installed for authenticated byte PDF viewing; focused analyzer clean; 88 feature/session regression tests passed. APK native build verification logged separately.
- Runtime feature defaults false in source configuration; enabled only in the new local API 5022 after migration. Existing Docker API 5000 was retained. SMTP and OTP pepper are configured in the local runtime, but delivery to a real mailbox was not exercised by the automated proof.
- Deferred limitations: authorized Staff document download is not part of this TO screen phase; production storage/deployment/retention policy and automated orphan reconciliation remain follow-up work. Private uncertain-commit artifacts must not be blindly deleted. No production mutation, commit or push performed.
- Final verification: 50 Web/Mobile login/refresh/API-guard regression tests passed. One initial concurrent-build attempt exhausted memory; rerun against built assemblies passed 50/50 after stopping Gradle. Mobile debug APK built successfully and its scoped analyzer plus 88 feature/session tests passed. APK not installed; real email and interactive Android signing remain manual checks. The local new API listens on 5022 with the gate enabled and the current FE runs at 3002.
