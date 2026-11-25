# Plan — Admin contract template management
Status: DRAFT, pending approval. Date: 23/11/2026.
Branch: fix/linhnv-operator-sign-up.
Specification: ../specs/admin-contract-template-management-spec.md.

## P0. Scope and existing work
Preserve staged/unstaged UI/date/alignment changes in BE/FE/Mobile. Record baseline.
Confirm official DOCX, placeholder schema, limits, draft deletion and immediate publication policy.
Read AGENTS, design-system skill and existing Admin content/upload/audit patterns. Mobile administration is out of scope.
Do not start implementation until approval. Contract signing/OTP remains a separate feature.

## P1. Renderer and DOCX feasibility
Using synthetic fixtures, demonstrate OOXML validation, split-run placeholder extraction/replacement, Vietnamese fonts/tables/page breaks, preview watermark and deterministic signature-region metadata.
Verify renderer licensing, Linux/container availability, isolation, timeout and memory limits. Confirm private storage and authorized streaming.
Tests: disguised DOCX, external relationships, embedded files, ZIP traversal/bomb limits, invalid placeholder and split-run token.
Completion: select engine/adapter and document actual limits; stop if signature anchor cannot be preserved. Do not install an arbitrary engine and claim production readiness.

## P2. Domain and database — BE
Proposed files:
- src/TripMate.Domain/Entities/OperatorContractTemplate.cs
- src/TripMate.Domain/Entities/ContractTemplateRenderJob.cs
- Domain enum/value types for state, template type and language.
- Infrastructure/Persistence/Configurations/OperatorContractTemplateConfiguration.cs
- Infrastructure/Persistence/Configurations/ContractTemplateRenderJobConfiguration.cs
- ApplicationDbContext/IApplicationDbContext registrations.
- database/migrations/<approved-date>_admin_operator_contract_templates.sql.
Reuse the template entity planned in operator-contract-signing; do not create two template tables.

Test first: Draft update increments revision; Published/Archived immutable; invalid publish forbidden; one Published per type/language; new version leaves original untouched.
SQL migration tests: fresh/reapply/existing data, unique filtered index and FK/reference protection.
Completion: idempotent database-first migration; no account-status change. Back up before local application.

## P3. Private storage and document validation — BE
Create Application ports for document inspection/render/storage only where existing abstractions are insufficient.
Infrastructure adapters under Contracts/Templates, following P1 engine choice. Treat files as untrusted, sanitize names and enforce actual decompressed limits.
Separate source/preview hashes, staged writes and reference-aware cleanup; do not hold DB locks over external I/O.
Tests: failed upload, wrong MIME/structure, orphan recovery and authorized download.
Completion: no public artifact URL, no loss of current published source.

## P4. Draft/read/download slices — BE
Features/Admin/ContractTemplates/{List,Get,CreateDraft,UpdateDraft,Download}: query/command + validator + handler.
V1 AdminContractTemplatesController: Administrator-only, thin mapping to Result/ProblemDetails.
Units: list/get; create; replace file; private download; clone version; optional delete Draft.
Tests: role/owner access, invalid variables, stale row version, edits to Published rejected, Draft deletion with references rejected.
Completion: stable metadata DTO for FE without internal storage keys.

## P5. Preview jobs — BE
Features/Admin/ContractTemplates/GeneratePreview with job claim/lease/background adapter; reuse outbox/job patterns.
Capture immutable revision/hash; implement render -> private upload -> conditional Ready commit.
Signature-anchor coordinates must be resolved again on the final TO-specific PDF; the sample preview coordinates are not reusable signing positions.
Tests: stale completion discarded, two requests deduplicated, worker crash/retry, timeout, invalid file, cleanup.
Completion: preview Ready only for current revision; watermark and signature anchor verified; source change resets acknowledgment.

## P6. Publication and history — BE
Features/Admin/ContractTemplates/{Publish,CreateVersion}; publication transaction archives current and publishes candidate with audit.
Add a publication-head concurrency token and expectedCurrentPublishedId; test simultaneous publication of two different Drafts against the same head, not only two requests for one Draft.
Tests: preview-not-ready, wrong hash/revision, unauthorized caller, concurrent publish exactly one winner, retry idempotency, storage failure and old-version preservation.
Completion: one current Published and retrievable immutable history. No Staff approval/account activation side effect.

## P7. Admin web — FE
Proposed:
- app/admin/(console)/contract-templates/page.tsx and [id]/page.tsx.
- src/features/admin/contract-templates/{ContractTemplatesScreen,ContractTemplateEditor,TemplatePreviewPanel,contractTemplates,contractTemplatesService,contractTemplatesProxy,resources}.
- app/api/admin/contract-templates/... BFF routes for list/detail/upload/preview/download/publish/version and optional delete.
- src/lib/routes.ts, AdminSidebar and existing breadcrumbs/navigation definitions.
Confirm exact paths before adding; reuse shared controls/banners/modals.

Units: list/status/pagination -> draft upload/validation -> editor/revision conflict -> preview processing -> publish confirmation -> version history/download.
Behavior tests: authorization/BFF forwarding, form error mapping, stale preview, duplicate click, 409 refresh and published read-only.
Visually check desktop/narrow layout, long filenames, file input, preview fallback, keyboard and loading/empty/error states.
Per user preference, no automated tests solely for CSS/position changes; business/state/upload behaviors still require tests.

## P8. Verification and handoff
Run BE build and focused domain/application/API/SQL tests, FE lint/typecheck/relevant tests/build; report missing external services and existing security/build issues honestly.
E2E with synthetic DOCX: Admin create -> validation -> preview -> publish v1 -> clone -> edit -> publish v2 -> inspect unchanged v1.
Verify Staff/TO denial, two Admin sessions publishing, old preview completion, download security and existing contract-reference retention.
Perform independent specification compliance and code-quality review.
Update configuration examples with placeholders, engine/fonts/deployment requirements and official-template ownership.
Hand off published-template query/DTO/hash/anchor contract to the later TO signing plan.
Do not enable waiting-signature account gate yet. No production mutation, commit/push/PR/deploy without separate authorization.

## Execution rule
Each unit follows test red -> minimal implementation -> verify -> review.
Do not create a generic CMS, new financial rules, legal contract wording or Mobile Admin feature.

## Execution record — 23/11/2026
- Domain/workflow: 14 focused tests passed; includes v2 archival, stale publication head and exhausted rendering lease recovery.
- Infrastructure: 25 focused tests passed; separate real Docker adapter run: 3 passed, including DOCX preparation, actual PDF generation and signature coordinates.
- SQL Server: 4 migration/conflict tests passed against disposable test databases.
- Local database: COPY_ONLY/CHECKSUM backup verified and copied to `../backups/TripMateDb-before-contract-templates-20261123-a5bd3992.bak`; migration applied and recorded Applied; all 66 users preserved.
- Local authenticated HTTP: Administrator login/list 200, anonymous list 401; synthetic Draft 1 rendered Ready and private PDF download returned 200 (24078 bytes). No synthetic template was published.
- Local runtime: new API port 5022. Existing Docker API/FE were retained; old FE port 3001 points at another worktree. New FE uses port 3002.
- Review: domain/persistence reviewer identified exhausted lease and publication-conflict handling; both fixed. Parent performed specification/security and code-quality passes after agents became unavailable.
- Remaining validation: interactive responsive/keyboard review and real concurrent publication through two API sessions have not been performed. SQL constraints and sequential stale-head behavior are covered.
- Artifact retention: files are private; automatic orphan reconciliation after uncertain commits is not enabled. Do not purge files manually because version clones share source keys. Retention/deployment hardening remains follow-up work.
- TO signing, OTP, account activation and production deployment remain outside this implementation.
- Final builds: BE Release succeeded with 0 warnings/errors; FE webpack production build succeeded after `npm ci` restored the existing locked Next 16.4.5. No package/lockfile version changes. FE focused 21 tests passed again with locked dependencies.
