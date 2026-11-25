# TM-82 / UC-36 - Update Tour Package (Draft, revision 5)

Status: **Approved by the Task Owner (Mai Nguyễn Tiến Đạt) on 2026-11-25.** Cleared to move into
the plan and implementation phases (AGENTS.md §1.2 steps 2-4). This revision replaces revision 4
and records four further decisions the Task Owner made alongside clearing implementation to begin
(§3, point H) — a deliberate departure from UC-60/UC-61's notification-retry wording (H.D1), a
closed scope boundary on tour media (H.D2), a clarified BR-101 field list for the draft version
(H.D3), and no change to §3 point B/G5's locked transaction shape (H.D1 only narrows what "stop and
ask" in that section still covers). Revision 4's own content is otherwise unchanged; see §13 for the
full diff.

## 0. Why this spec exists

A code audit of the existing `PUT /api/v1/operator/tours/{tourId}` endpoint found that the shipped
behavior only covers direct editing of a `Draft`/`Rejected` tour
(`UpdateTourDraftCommandHandler.cs:46-47`, **VERIFIED** — guard is exactly
`if (tour.Status is not (TourStatus.Draft or TourStatus.Rejected))`, same guard repeated on every
domain mutator in `Tour.cs`). There is no path to edit a tour that is already `Approved` or
`Published`, and nothing re-triggers Admin review when an operator changes a field on a live tour.
This matches the SRS's own UC-36 Alternative Flow (§1.1 below), which the current code does not
implement at all.

**Separately, the existing `CreateTourPackageCommandHandler`'s `SourceTourId` path** (which creates
a brand-new `Tour` row carrying `VersionRootTourId`/`VersionNo` lineage from an
Approved/Published source tour) **already exists in the codebase today and is unrelated to this
ticket's fix.** TM-82 does not change, remove, or depend on that path (§10's closed Open item 1
chose the lighter-weight `TourPendingChangeSet` entity instead of reusing it). The pre-existing gap
that path has — approving a new version from it can leave two `Published` rows for the same logical
tour live at once, since nothing supersedes the older row — is a real, already-existing defect,
**out of scope for TM-82**, and should be tracked as its own separate ticket.

## 1. Sources and precedence

1. **Primary source, with an explicit PDF/DOCX reconciliation (requested this revision).** Two SRS
   files exist: `d:\CapStone\Report3_Software-Requirement-Specification.pdf` (289 pages, file
   mtime 2025-09-23) and `d:\CapStone\Report3_Software-Requirement-Specification.docx` (file mtime
   2026-11-04, i.e. newer). Both were read directly this revision (PDF via `pdftotext -layout`
   against the exact page range; DOCX via its own `word/document.xml`, since no python-docx or
   LibreOffice was available in this environment — the XML was parsed directly, not guessed).
   **Finding: for UC-36 and UC-37 the two files agree verbatim. For UC-60/UC-61 they genuinely
   disagree** — see §1.3. Where they disagree, this spec records both and states which one it
   follows and why, rather than picking silently.
2. The UC chapter body text (§3.8.2.2, §3.8.2.3, §3.9.7.1, §3.9.7.2) is used in preference to the
   Appendix §5.1 summary table throughout, confirmed again this revision (§1.4, Open item 2 — now
   closed).
3. Jira TM-82 (`[UC-36] Update Tour Package`) for ticket scope/status only.
4. Current `origin/develop` code for architecture and conventions: `Tour.cs`, `TourStatus.cs`,
   `UpdateTourDraftCommandHandler.cs`, `TourReviewCommandHandlers.cs`, `TourConfiguration.cs`,
   `ApplicationDbContext.cs`, `AuditActionTypes.cs`, `OperatorTourErrorCodes.cs`,
   `AdministratorInAppNotices.cs`, `CreateTourPackageCommandHandler.cs`, `Booking.cs`, `Review.cs`,
   `CouponTourEligibility.cs`, `TourVisibility.cs`, `database/tripmate_schema_v7.sql`,
   `database/migrations/20261015_add_tour_package_authoring.sql`.
5. Decisions explicitly confirmed by the Task Owner (recorded in §3). AGENTS.md §1.1 forbids
   inferring business rules, so every rule below is either a direct SRS quote, a Task Owner
   decision, or a VERIFIED code finding, never an unstated assumption.

### 1.1 Binding text extracted from the SRS — UC-36 and UC-37 (verbatim, PDF and DOCX agree)

**UC-36 "Update Tour Package", PDF p.154-157 (DOCX content matches).**

> Purpose: Allows a Tour Operator to modify an owned tour package, either directly while it is a
> draft or a rejected package, or through a new draft version when it is already approved and
> published.
>
> Data Processing: The system verifies the ownership of the tour package by the signed-in Tour
> Operator. The system determines whether the tour package may be edited directly on the basis of
> its current status. When the tour package is approved, the system creates a new draft version
> and the change is applied to that version only. The system validates the changed fields with the
> same rules as the creation. The capacity of a departure schedule is never reduced below the
> number of the slots already sold.

Business Rules (p.156):

> BR-102: A Tour Operator may modify only the tour packages owned by that Tour Operator.
> BR-103: An approved and published tour package is never edited in place; the modification is
> applied to a new draft version that must be submitted for approval again.
> BR-101: The changed tour package must still satisfy the completeness rule of the mandatory
> fields.
> BR-61: The capacity of a departure schedule must not be reduced below the number of the slots
> already sold for that schedule.
> BR-22: The departure date must not be earlier than the current date and the return date must not
> be earlier than the departure date.

Alternative Flow — "Modify an approved tour package through a new draft version" (p.156):

> 1. The Tour Operator opens an approved and published tour package for editing at step 1 of the
>    Normal Flow.
> 2. MSG110 is displayed together with the option to create a new draft version.
> 3. The Tour Operator confirms the creation of the new draft version.
> 4. The system creates the new draft version, keeps the published version unchanged and opens the
>    draft version for editing.
> 5. The Normal Flow continues from step 3, and the draft version must be submitted for approval by
>    UC-37.

Abnormal case 2.a2 (p.156): "The tour package is currently pending approval. MSG122 is displayed
and the tour package is opened in the **read-only mode**."

Post-Conditions (p.157):

> PC-01: The stored tour package or the stored draft version reflects the validated changed
> values.
> PC-02: The published version of an approved tour package remains unchanged until a new draft
> version has been approved.
> PC-03: The capacity of every departure schedule remains greater than or equal to the number of
> its sold slots.
> PC-04: The previous values remain unchanged when the validation fails or the save operation
> fails.

**UC-37 "Submit Tour Package for Approval", PDF p.157-159 (DOCX content matches).**

> Purpose: Submits a completed tour package to the Administrator for the moderation that decides
> whether the tour package may be published to the Travelers.

Business Rules (p.158-159):

> BR-104: A tour package may be submitted for approval only when its current status is Draft or
> Rejected.
> BR-102: A Tour Operator may submit only the tour packages owned by that Tour Operator.
> BR-101: A tour package may be submitted only when all its mandatory fields, at least one
> itinerary day and at least one departure schedule are provided.
> BR-103: A modification of an approved tour package reaches the Travelers only through a new
> draft version that has been submitted and approved again.
> BR-56: The tour package remains not discoverable by the Travelers while it is pending approval.

Abnormal case 2.a3 (p.159): "The tour package is already approved. MSG110 is displayed and the
Tour Operator is offered the creation of a new draft version by UC-36."

Post-Condition PC-02 (p.159): "The submission appears in the moderation queue of the Administrator
and is handled by **UC-60 or UC-61**."

### 1.2 Binding text — UC-60/UC-61 Validation tables and Abnormal Cases (verbatim, PDF and DOCX agree on these)

**These parts are identical in both files** — only the actor role (§1.3) differs.

UC-60 "Approve Tour Post" Validation table (p.228):

> An item of the mandatory review checklist has not been marked as passed → Display: MSG01
> The selected package is no longer in the Pending Approval status → Display: MSG133
> **The approval is confirmed and stored → Display: MSG144**
> The signed-in account does not hold the [role] role → Display: MSG126
> The decision cannot be stored because of a system or network failure → Display: MSG127

UC-60 Abnormal Cases (p.228-229), verbatim, same in both files:

> 1.a1. The signed-in account does not hold the [role] role: the system denies the access and
> displays MSG126.
> 2.a1. No package is waiting for a decision: the system displays MSG128 and shows an empty queue.
> 8.a1. An item of the mandatory review checklist has not been marked as passed: the system
> displays MSG01 and does not store the decision.
> 8.a2. The selected package is no longer in the Pending Approval status because it has already
> been decided by another [role]: the system displays MSG133 and refreshes the queue.
> 9.a1. The decision cannot be stored because of a system or network failure: the system displays
> MSG127, keeps the package in the Pending Approval status and allows a retry.
> **9.a2. The notification to the Tour Operator cannot be delivered: the system keeps the stored
> decision, queues the notification for a retry and does not block the flow.**

UC-61 "Reject Tour Post" Validation table (p.230-231), verbatim, same in both files:

> The rejection reason is empty → Display: MSG123
> The selected package is no longer in the Pending Approval status → Display: MSG133
> **The rejection is confirmed and stored → Display: MSG145**
> The signed-in account does not hold the [role] role → Display: MSG126
> The decision cannot be stored because of a system or network failure → Display: MSG127

UC-61 Abnormal Cases (p.231), verbatim, same in both files:

> 1.a1. The signed-in account does not hold the [role] role: the system denies the access and
> displays MSG126.
> 7.a1. The rejection reason is empty: the system displays MSG123 and does not store the decision.
> 7.a2. The selected package is no longer in the Pending Approval status: the system displays
> MSG133 and refreshes the queue.
> 8.a1. The decision cannot be stored because of a system or network failure: the system displays
> MSG127, keeps the package in the Pending Approval status and allows a retry.
> **8.a2. The notification to the Tour Operator cannot be delivered: the system keeps the stored
> decision and queues the notification for a retry.**

**VERIFIED this revision**: UC-61's success message is **MSG145**, not MSG144 — MSG144 belongs to
UC-60 (Approve) only, confirmed identically in both the PDF and the DOCX. MSG133 is confirmed
shared between UC-60 and UC-61 (the condition "no longer Pending Approval" uses the same code in
both). The notification-failure abnormal case is step **9.a2** in UC-60 and step **8.a2** in UC-61
(different step numbers because UC-61's Normal Flow has one fewer step before the decision is
stored); both are confirmed present, verbatim, in both files, with UC-60's version carrying an
extra clause ("...and does not block the flow") that UC-61's shorter sentence omits — a real wording
difference between the two use cases within the *same* file, not a PDF/DOCX discrepancy.

Business Rules referenced by UC-60/UC-61 besides role (p.228, p.230-231), same in both files:

> BR-104 (UC-60): Only a package whose status is Pending Approval may receive a decision; a package
> already decided cannot be decided again.
> BR-103 (UC-60): The approval applies to the submitted version; a later change by the operator
> requires a new draft version and a new approval.
> BR-119 (UC-61): A rejection requires a reason and the stored reason is visible to the owning Tour
> Operator.
> BR-104 (UC-61): The rejected package returns to a state from which the operator may correct it
> and submit it again.
> BR-118: The decision record stores the reviewing [role] and the decision timestamp.
> BR-130: The decision event is written to the immutable audit log.

**There is no separate use case for "approving a new draft version" distinct from "approving a
first-time submission."** PC-02 of UC-37 is explicit: every submission is "handled by UC-60 or
UC-61," the same moderation queue and the same two use cases.

### 1.3 PDF vs. DOCX disagreement on the moderating role — VERIFIED, read independently this revision

This is a genuine content difference between the two files, not a numbering or extraction error.

| | PDF (older, 2025-09-23) | DOCX (newer, 2026-11-04) |
|---|---|---|
| UC-60 Actors/Roles | "Administrator" | "Staff" |
| UC-60 BR-115 | "Only an account holding the **Administrator** role may moderate a tour package." | "Only an account holding the **Staff** role may moderate a tour package." |
| UC-60 Validation row "does not hold the role" | "...Administrator role → MSG126" | **Still says "...Administrator role → MSG126"** — not updated to Staff, while the rest of UC-60 in the DOCX was |
| UC-60 Abnormal 1.a1 | "...Administrator role..." | "...Staff role..." |
| UC-60 Abnormal 8.a2 | "...decided by another **Administrator**..." | "...decided by another **Staff** ..." |
| UC-60 PC-02 | "...reviewing Administrator..." | "...reviewing Staff ..." |
| UC-61 Actors/Roles, BR-115, Validation, Abnormal Cases, PC-02 | all "Administrator" | all "Staff" — **internally consistent** in the DOCX (unlike UC-60, UC-61 has no leftover "Administrator" line) |
| UC-37 PC-02 (separate location in the DOCX, not inside UC-60/61 itself) | "moderation queue of the Administrator" | **also says "Administrator"**, not "Staff" |

The DOCX is the more recently edited file and appears to be mid-migration from an "Administrator"
moderates everything" model toward a "Staff" role for tour moderation specifically — but the edit
is incomplete even within the DOCX itself (UC-60's own Validation table line still says
"Administrator" while the rest of that same use case in the same file says "Staff"; and UC-37's
separate PC-02 reference to the "moderation queue of the Administrator" was not updated either).

**Per the Task Owner's locked decision (§3, point A3): this spec maps "Staff" to the existing
`UserRole.Administrator`**, matching every currently-shipped review handler
(`TourReviewCommandHandlers.cs`'s Approve/Reject/Publish/Unpublish all gate on
`currentUser.Role != nameof(UserRole.Administrator)`, **VERIFIED**). This is recorded as a
**deliberate implementation mapping, not a claim that the SRS is internally consistent** — if the
project later introduces a true separate `Staff` role distinct from `Administrator`, UC-60/UC-61's
moderation endpoints (both the existing ones and this spec's new draft-version ones) would need a
follow-up ticket to re-target that role. This spec does not invent a new role to resolve the
document's own inconsistency.

### 1.4 Appendix §5.1 vs. UC body text — now closed (was Open item 2)

SRS Appendix §5.1 has differently-worded BR-94/95/96 entries, including a BR-96 reading "changes
that affect approval-sensitive fields must route back through the approval workflow" — which could
be read as a weaker, field-scoped version of "all fields require review." **The Task Owner has now
confirmed (§3, point E1) that the UC-36/37/60/61 body text is authoritative**, specifically BR-103
("never edited in place... must be submitted for approval again," with no field-level carve-out
anywhere in that sentence). The Appendix's differently-scoped BR-96 is noted here as a known
inconsistency in the source document, not acted on.

The message codes this spec uses, all confirmed by direct "Display: MSGxx" lines inside
UC-36/UC-37/UC-60/UC-61, with the UC-61-specific corrections applied this revision:

| Code | UC source | Verbatim meaning |
|---|---|---|
| MSG01 | UC-36, UC-37, UC-60 | A mandatory field/checklist item is empty or not marked passed |
| MSG29 | UC-36 | A departure date or a return date is invalid |
| MSG109 | UC-36 | The tour package is updated successfully |
| MSG110 | UC-36, UC-37 | The tour package is Approved; offers creating a new draft version instead of direct edit |
| MSG111 | UC-37 | The tour package is submitted successfully |
| MSG122 | UC-36, UC-37 | The tour package is currently pending approval (UC-36: opens read-only; UC-37: blocks a second submission) |
| MSG123 | UC-61 | The rejection reason is empty |
| MSG126 | UC-36, UC-37, UC-60, UC-61 | The signed-in account is not the owner / does not hold the required role |
| MSG127 | UC-36, UC-37, UC-60, UC-61 | The change/submission/decision cannot be saved (system or network failure) |
| MSG128 | UC-60 | No package is waiting for a decision (empty queue) |
| MSG133 | UC-60, UC-61 | The selected package is no longer Pending Approval (already decided) — **shared by both** |
| MSG143 | UC-36 | The capacity is reduced below the number of the sold slots |
| **MSG144** | **UC-60 only** | **The approval is confirmed and stored** |
| **MSG145** | **UC-61 only** | **The rejection is confirmed and stored — corrected this revision; was previously miscited as MSG144** |

## 2. Scope

### In scope

- A new way for a Tour Operator to edit a tour they own that is currently `Approved` or
  `Published`, implemented as a **new draft version** (`TourPendingChangeSet`) — per UC-36's
  Alternative Flow, not as an in-place edit (BR-103).
- The draft version's own small lifecycle: `Draft` → `PendingReview` → `Applied` | `Rejected`, and
  `Rejected` → edited again → `PendingReview` (resubmission).
- Saving changes to the draft version before submission (UC-36 Normal Flow, applied to the draft
  version per the Alternative Flow).
- Submitting the draft version for approval (UC-37), landing in the **same** Admin moderation queue
  UC-60/UC-61 already use for first-time `Pending` tours (UC-37 PC-02), via a **new, additive read
  endpoint** (§5.6) rather than a change to the existing queue's contract (§3, point E2).
- Admin approve/reject of a `PendingReview` draft version, reusing UC-60/UC-61's own rules (role
  check mapped to `Administrator` per §1.3, single-decision check, reason required on reject,
  in-app notification to the operator on both approve and reject, with failed notification delivery
  never blocking or reversing the stored decision — §3, point B).
- The live `Tour` (BR-103 "never edited in place") and its destinations/itinerary/schedules/
  cancellation policy are unchanged while the draft version exists in `Draft`, `PendingReview`, or
  `Rejected` — UC-36 PC-02.
- The BR-61/MSG143 capacity guard, enforced at save, submit, and approve (§6.4).
- The **past-departure schedule immutability rule** (§3, point D) — new this revision, closing the
  BR-22-vs-BR-61 deadlock a completed tour would otherwise hit.
- A small, additive change to the **existing** direct-edit `PUT /api/v1/operator/tours/{tourId}`
  endpoint (UC-35): calling it against a tour that is `Approved` or `Published` now returns
  `operator_tour.invalid_state` with **MSG110** (§3, point A2) instead of whatever it currently does
  for that status, pointing the operator at the new "create a draft version" flow instead. Its
  behavior for `Draft`/`Rejected` tours is **unchanged** — this is strictly a new response for a
  status combination that endpoint does not correctly handle today.
- Audit logging for create-draft / save-draft / submit / approve / reject / discard.

### Out of scope

- Any change to how a tour that is still `Draft`/`Rejected` **itself** is edited directly — UC-36's
  Normal Flow, already implemented. (The one exception is the small additive MSG110 response for
  `Approved`/`Published` noted above, which does not touch the `Draft`/`Rejected` code path.)
- Withdrawing or deleting a submission that is already `PendingReview`. **Not found anywhere in the
  SRS.** If a traceable source authorizing this is found later, it is a separate use case/ticket,
  not part of TM-82.
- The existing `CreateTourPackageCommandHandler`'s `SourceTourId`/`VersionRootTourId`/`VersionNo`
  path (§0). TM-82 does not modify, remove, or reuse it (§10's Open item 1 is now closed in favor of
  a separate `TourPendingChangeSet` entity). Its own pre-existing two-Published-rows gap is a
  separate, already-existing defect, to be tracked on its own ticket, not fixed here.
- A generic diff/workflow engine that decides per-field whether review is "really needed" — closed,
  §1.4.
- Changing the `TourStatus` enum or the `commerce.Tours.status` SQL CHECK constraint. **VERIFIED**:
  the live constraint (`database/migrations/20261015_add_tour_package_authoring.sql:40-41`) already
  allows `'Published'` — `CHECK (status IN ('Draft','Pending','Approved','Rejected','Published','Inactive'))`.
  (The checked-in baseline file `database/tripmate_schema_v7.sql:519-520` predates that migration
  and omits `'Published'`; it is not the current database state and is not used as a source here.)
- Introducing a distinct `UserRole.Staff` separate from `Administrator`. §1.3's locked mapping
  reuses `Administrator`, matching every existing review handler.
- Email notification — in-app only (§3, point A3 was the role mapping; the channel decision is
  carried over unchanged from revision 2: UC-60's text names no channel, and the existing
  `Notification` entity/`AdministratorInAppNotices` pattern is in-app).
- Retroactively adding notifications to the **existing** first-time-Pending-tour Approve / Reject /
  Publish / Unpublish handlers in `TourReviewCommandHandlers.cs`. **Confirmed by the Task Owner
  (§3, point E3) as a pre-existing gap, out of scope for TM-82, to be tracked as a separate ticket.**
- Mobile/Web UI implementation (backend-only ticket).

## 3. Decisions locked by the Task Owner this revision

### A. Error contract corrections

1. **A1 — UC-61 success code.** Reject-success uses **MSG145**, not MSG144 (MSG144 is Approve-only).
   VERIFIED in both SRS files, §1.2/§1.4. Empty rejection reason uses **MSG123** (VERIFIED). A
   decision attempted on a version no longer `PendingReview` uses **MSG133** for both approve and
   reject (VERIFIED, shared code).
2. **A2 — MSG110's actual meaning.** MSG110 means "the tour package is Approved; the operator is
   offered the option to create a new draft version instead of editing in place" (UC-36 Alternative
   Flow step 2; UC-37 abnormal case 2.a3) — it is **not** a generic "invalid state" error. This
   spec's error contract (§7) now routes each tour status correctly instead of overloading MSG110:
   - A tour that is `Draft`/`Rejected` is edited through the existing UC-35 direct-edit endpoint,
     unaffected by this spec.
   - A tour that is `Pending` returns `operator_tour.invalid_state` with **MSG122** (currently
     pending approval), matching UC-36 abnormal case 2.a2 verbatim.
   - The **existing direct-edit `PUT /api/v1/operator/tours/{tourId}` endpoint** (UC-35/UC-36
     Normal Flow), when called against a tour that is `Approved` or `Published`, returns
     `operator_tour.invalid_state` with **MSG110** — exactly UC-36's Alternative Flow trigger
     condition — together with (in the response) the fact that a new draft version can be created
     instead. This spec's new `POST .../pending-changes` endpoint (§5.1) **is** that "create a new
     draft version" action UC-36 offers in response to MSG110; it does not itself return MSG110 on
     success.
3. **A3 — Role citation.** BR-115 and the UC-60/UC-61 role lines read "Staff" in the DOCX (the
   newer file) and "Administrator" in the PDF (the older file) — a genuine disagreement, not a
   transcription error (§1.3). **This spec maps "Staff" onto the existing `UserRole.Administrator`**,
   matching every currently-shipped tour-moderation handler. This is recorded as a deliberate
   implementation choice, not a resolution of the SRS's own inconsistency — flagged for the Task
   Owner's awareness, not as an open item blocking this spec (the mapping itself is decided; only a
   future true-Staff-role split would need its own ticket).

### B. Notification failure must never roll back a stored decision

Per UC-60 abnormal case 9.a2 and UC-61 abnormal case 8.a2 (§1.2, verbatim, identical in both SRS
files): **the decision (Approve or Reject) is always persisted first; a failure to deliver the
notification is queued for retry and never blocks, reverses, or fails the moderation decision
itself.**

**Analysis of whether the existing `AdministratorInAppNotices.AddAsync` helper already satisfies
this — code-only, no new design invented here:**

- **VERIFIED**: `AdministratorInAppNotices.AddAsync(dbContext, type, title, body,
  relatedEntityType, relatedEntityId, nowUtc, ct)` only calls `dbContext.Notifications.Add(...)` —
  it stages the `Notification` row on the tracked `DbContext` and returns; it does **not** call
  `SaveChangesAsync` itself (the caller does, once, for the whole unit of work).
- **VERIFIED**: every existing caller of this helper (and the equivalent `EmergencyPushNotices`)
  adds the notification row into the **same `SaveChangesAsync`** as the business-state change it
  is about (e.g. the tour-status transition), inside the same database transaction.
- **Consequence (VERIFIED from the above, not assumed):** today, if that single combined
  `SaveChangesAsync` fails for any reason (including a reason specific to the `Notification` insert,
  e.g. a constraint violation), **the entire transaction rolls back — including the decision itself.**
  There is no separate, independently-retriable notification delivery step; "delivery" here means
  nothing more than "the row was written to `dbo.Notifications`," and that write currently shares
  fate with the decision write.
- **This does not yet satisfy UC-60/61's requirement** that a *notification* failure specifically
  must not block the *decision*. The current mechanism cannot fail the notification write
  independently of the decision write, because they are one atomic unit — so there is no code path
  today where "the decision succeeded but the notification failed and was queued for retry" can
  actually happen. Whether an in-process `Notification` row insert can even fail independently of
  the surrounding transaction (as opposed to a genuinely separate delivery channel like a push
  provider or email service, which can fail for reasons unrelated to the database transaction) is
  also unverified — the current `Notification` entity has no documented external delivery leg at
  all; `NotificationChannel`/`NotificationStatus` were inspected only enough to confirm they exist
  (§1 source list), not studied for a retry mechanism.
- **Confirmed shape for the fix (Task Owner instruction, applied this revision):** the decision
  (Approve or Reject) and its audit log row are written and committed together in **one**
  serializable transaction (BR-130 requires the decision event in the audit log, so the two belong
  together). The notification is attempted in a **separate step after that transaction has already
  committed**. If the notification attempt fails, the already-committed decision is **not** rolled
  back — the failure is only ever a notification-delivery concern, never reopens or reverses the
  moderation outcome. This is a shape decision, locked by the Task Owner; the specific mechanics
  (synchronous best-effort attempt vs. a queued retry, and what happens to a `Notification` row that
  failed to insert) are still for the plan phase to choose, per the next paragraph.
- Per the Task Owner's explicit instruction, **no outbox or background-job infrastructure is
  designed in this spec.** `plans/TM-82-plan.md` must pick the smallest mechanism that satisfies
  UC-60 9.a2/UC-61 8.a2 using entities that already exist (the `Notification` entity itself), and
  must **stop and ask the Task Owner** before implementation if it concludes new infrastructure
  (an outbox table, a background worker, a message queue) is actually required.

### C. "New draft version" vs. UC-36 PC-02 and UC-37/BR-56's Pending Approval wording — confirmed reading

UC-37 says a submission "sets the status of the tour package to Pending Approval," and BR-56 says
"the tour package remains not discoverable by the Travelers while it is pending approval" — read
in isolation, this could be misread as applying to the *live* tour the operator already published.
That would contradict UC-36 PC-02 ("The published version of an approved tour package remains
unchanged until a new draft version has been approved").

**Confirmed reading (Task Owner has explicitly signed off on this interpretation):** "Pending
Approval" status belongs to the **draft version**, not to the live tour. The live, already-published
tour is never hidden and never stops accepting bookings while its draft version is anywhere in
`Draft`/`PendingReview`/`Rejected`. UC-37/BR-56's "not discoverable while pending" describes a
tour's **own first-time** submission (where there is no separately-published live version yet to
protect) — for a draft version of an already-`Approved`/`Published` tour, UC-36 PC-02 is the more
specific rule and prevails. This reading is what §6.3 ("Live tour isolation") already implemented
in revision 2; it is now explicitly marked as Task-Owner-confirmed rather than this spec's own
inference.

### D. Past-departure schedules are immutable inside a draft version (new this revision)

**Problem identified:** BR-22 forbids a departure date earlier than the current date; BR-61
forbids reducing a schedule's capacity below its already-sold slots. A tour that has run to
completion has at least one `TourSchedule` whose departure date is now in the past **and** which
likely has sold slots. Carrying that schedule into a draft version's proposed payload and
re-validating it against BR-22 (now in the past) would always fail — but dropping it to satisfy
BR-22 would violate BR-61 (a schedule with sold slots cannot be removed). Neither rule alone
describes this case; the SRS does not address it. **Confirmed business reading, approved by the
Task Owner:**

- A `TourSchedule` whose departure date is already in the past, as of the moment a draft version is
  created, is **read-only / immutable** inside that draft version: it is always carried over
  verbatim with its **original `TourSchedule.Id`** and original field values — never re-validated
  against BR-22 (it is not a "new" or "changed" schedule) and never eligible for removal by the
  draft version (so it can never trip the BR-61 "would this change remove a schedule" check either,
  since it is never treated as removed).
- BR-22 applies only to a schedule that is **new** (added by this draft version) or **future and
  edited** (an existing schedule whose departure date is still in the future and whose proposed
  values differ from the live one).
- BR-61 continues to apply normally to every schedule, past or future, that the draft version's
  payload still contains with the matched original id — the point above only changes which
  validations run against a past schedule, not whether BR-61 still checks it.

This closes the deadlock: an operator editing an Approved/Published tour that has already completed
some departures can still submit a draft version (e.g. to change pricing or itinerary for future
departures) without being forced to either falsify a past date or delete a schedule that already
sold seats.

### E. Open items resolved this revision

1. **E1 (was Open item 2).** Appendix BR-96 vs. UC body text BR-103: **UC body text wins.** All
   fields on a draft version require Admin approval; no field-level carve-out. §1.4.
2. **E2 (was Open item 3).** The Admin moderation queue is **one logical queue** containing both
   first-time `Pending` tours and `PendingReview` draft versions, with a classification field (e.g.
   `"kind": "NewSubmission" | "DraftVersion"`) distinguishing the two, paginated per CR-01 — per
   UC-37 PC-02's "handled by UC-60 or UC-61." **Delivered as a new, additive read endpoint** (§5.6)
   rather than a change to the existing queue endpoint's contract, to avoid breaking any current
   Admin UI consumer of that endpoint. If the plan phase finds this additive approach impractical
   (e.g. the existing endpoint's consumer cannot easily call two endpoints), it must say so
   explicitly and get a new decision before changing the existing contract — this spec does not
   pre-authorize that.
3. **E3 (was Open item 4).** Confirmed: not adding notifications to the existing first-time-Pending
   Approve/Reject/Publish/Unpublish handlers is **intentionally out of scope for TM-82** — a
   pre-existing gap, to be tracked as its own separate ticket. Recorded in §2 Out of scope.
4. **Previously-locked points carried over unchanged from revision 2** (mechanism, lifecycle,
   read-only-while-pending, operator/admin endpoint shapes, validation placement at
   save/submit/approve, in-app notification requirement itself, JSON-column storage with retained
   schedule ids): unchanged, restated in §4-§8 below.

### F. Still explicitly out of scope (restated per instruction)

Withdrawing or deleting a version that is already `PendingReview`: **not found in the SRS.** If a
traceable source is found later, it is handled by its own use case/ticket, not by TM-82.

### G. Task Owner approval record (2026-11-25) and final decisions

**Approved by the Task Owner, Mai Nguyễn Tiến Đạt, on 2026-11-25.** This spec (as corrected by
points G1-G6 below and the edits they produced throughout §0, §2, §5, §7, §8, §9, §10) is cleared
to proceed to the plan and implementation phases per AGENTS.md §1.2.

1. **G1 — Open item 1 is now closed.** The Task Owner chose the lightweight `TourPendingChangeSet`
   entity (§4.1), explicitly **not** reusing `VersionRootTourId`/`VersionNo`. §10 is updated to
   record this as a closed decision, not an open question.
2. **G2 — `Inactive` tours.** Creating a draft version against a tour whose `Status` is `Inactive`
   returns `operator_tour.invalid_state`. Handling `Inactive` tours more specifically (e.g. whether
   they should ever be editable again) is **out of scope for TM-82** — this spec only needs the
   generic invalid-state rejection, not a dedicated flow.
3. **G3 — `operator_tour.pending_change_conflict` removed.** Create (§5.1) is already idempotent —
   a second create call simply returns the existing open draft version — so there is no longer a
   "conflict" case to report; the error code is removed from §7.
4. **G4 — §5.1's condition table rewritten** for clarity: `Draft`/`Rejected` → use the existing
   UC-35 direct-edit path (`operator_tour.invalid_state`, no draft version created);
   `Pending` → `operator_tour.invalid_state` + MSG122; `Inactive` → `operator_tour.invalid_state`
   (G2); only `Approved`/`Published` creates (or resumes) a draft version. See §5.1.
5. **G5 — Decision-then-notify transaction shape locked** for Approve/Reject (§3 point B, updated
   above): the decision and its audit log commit together in one serializable transaction;
   notification is a separate, later step whose failure never rolls back the decision.
6. **G6 — Data-integrity additions**, confirmed necessary and added to §8/§9:
   - A unique filtered index ensuring at most one **open** (`Draft`/`PendingReview`/`Rejected`)
     `TourPendingChangeSet` per `TourId` — enforced at the database level, not only in application
     code.
   - Every schedule id referenced inside a `TourPendingChangeSet`'s `SchedulesJson` payload must
     belong to the **same** `TourId` the change set is for; a schedule id belonging to a different
     tour is rejected, not silently accepted.

### H. Task Owner decisions recorded at implementation kickoff (2026-11-25)

1. **H.D1 — Notification retry: accepted as out of scope for TM-82, deliberately not matching
   UC-60 9.a2 / UC-61 8.a2's literal wording.** UC-60 9.a2 says the notification is "queued for a
   retry"; UC-61 8.a2 says the same. §3 point B/G5 already locked the shape (decision commits
   first, notification is a separate later step, its failure never reopens the decision) and left
   the retry *mechanics* for the plan phase, with an instruction to stop and ask before adding an
   outbox/worker/queue. **The Task Owner has now decided the mechanics themselves, closing that
   question rather than leaving it to the plan phase: there is no retry.** The notification is
   attempted exactly once; if that attempt fails, the failure is logged at **Error** level (tagged
   with the `TourPendingChangeSet` id and the acting user id) and the row is not retried by any
   later process. This is recorded here as a **known, deliberate divergence from the SRS's literal
   "queues... for a retry" wording** for TM-82 specifically — not a reading of the SRS, not an
   inference, and not something the plan phase chose on its own. A real retry mechanism (outbox
   table, background worker, or queue) is explicitly deferred to its own separate ticket. Because
   this decision already resolves what Task 7 step 4's stop condition was waiting on, that stop
   condition is narrowed: Task 7 only needs to stop and ask if implementation surfaces an
   infrastructure need **other than** "no retry, log and move on" (e.g. a need for a new background
   service unrelated to retry, or a schema change beyond logging). Plain retry infrastructure is no
   longer an open question requiring escalation — it is decided: **there is none**.
2. **H.D2 — Tour media is out of scope for TM-82; draft versions carry no media.** A
   `TourPendingChangeSet` has no media/image columns or relationship (§4.1 is unchanged by this
   point — it never had one). An operator's existing `Approved`/`Published` tour's photos cannot be
   changed through a draft version; media editing for an already-approved tour remains unavailable
   until/unless a future ticket adds it. This is recorded as an **Out of scope** limitation (§2),
   not silently omitted.
   - **Mandatory Task 0 check, performed this revision, VERIFIED, no stop condition hit:** every
     tour-media-mutating endpoint already gates on tour status today, independently of TM-82.
     `TourMediaAccessResolver.AllowsMaterialChange(TourStatus status)`
     (`src/TripMate.Application/Features/TourMedia/Common/TourMediaAccess.cs:43-44`) returns true
     only for `TourStatus.Draft` or `TourStatus.Rejected`, and is called by all four mutating
     handlers: `UploadTourMediaCommandHandler.cs:310` (inline equivalent check,
     `tour.Status is not (TourStatus.Draft or TourStatus.Rejected)`), `DeleteTourMediaCommandHandler.cs:52`,
     `ReorderTourMediaCommandHandler.cs:38`, and `UpdateTourMediaMetadataCommandHandler.cs:53`. An
     `Approved`/`Published` tour's media **cannot** be uploaded, deleted, reordered, or have its
     metadata changed through any existing endpoint today. **There is no BR-103 bypass through the
     media endpoints** — the stop-and-report condition this point's check was designed to catch did
     not trigger, and no code change is needed or made here.
3. **H.D3 — BR-101 for a draft version's completeness check (Task 6) uses the plain SRS list, not
   `TourReviewReadinessEvaluator`'s six facts.** UC-37 BR-101 (§1.1) and UC-35 BR-101 both read: all
   mandatory fields are present, at least one itinerary day, and at least one departure schedule.
   `TourReviewReadinessEvaluator.Evaluate(...)`
   (`src/TripMate.Application/Features/Operator/Tours/Common/TourReviewReadinessEvaluator.cs:35-45`)
   checks six facts, including `PrimaryImageMissing` — a check against the tour's **media** table,
   confirmed at `TourReviewReadinessEvaluator.cs:57-60` (`dbContext.TourMedia...AnyAsync(media =>
   media.IsPrimary...)`). Per H.D2, media does not exist on a `TourPendingChangeSet`, so this
   specific fact **does not apply** to the draft version's own completeness check — there is no
   primary-image gate for submitting a draft version, because there is no draft-version media at
   all. Task 6 must evaluate exactly the three SRS-named conditions (mandatory fields, ≥1 itinerary
   day, ≥1 departure schedule) against the change set's own stored fields/JSON payload, and must
   **not** import `TourReviewReadinessEvaluator` wholesale or otherwise re-introduce a media check
   for the draft version. If, once code is in front of the implementer during Task 6, this reading
   appears to conflict with something in the codebase, **stop and ask — do not guess** (per this
   point's own instruction and AGENTS.md §1.1); this spec does not pre-resolve a conflict that has
   not yet been found.

## 4. Domain model

### 4.1 New entity: `TourPendingChangeSet` (the "draft version")

New file `TripMate.Domain/Entities/TourPendingChangeSet.cs`. Private setters; factory
`CreateDraft(...)`; behavior methods `SaveDraft(...)`, `Submit(...)`, `MarkApplied(...)`,
`Reject(...)`. No `Cancel`/`Discard` status-transition method — discard (§5.4) deletes the row
outright while `Draft`/`Rejected`. No EF dependency (AGENTS.md §2.1).

```csharp
public enum TourPendingChangeStatus
{
    Draft = 1,
    PendingReview = 2,
    Applied = 3,
    Rejected = 4,
}

public class TourPendingChangeSet : BaseEntity
{
    public long TourId { get; private set; }
    public Tour Tour { get; private set; } = null!;
    public long SubmittedByUserId { get; private set; }
    public TourPendingChangeStatus Status { get; private set; }

    // Proposed scalar fields (same constraints as Tour.UpdateDraft)
    public int CategoryId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public decimal BasePrice { get; private set; }
    public int DurationDays { get; private set; }
    public string? Inclusions { get; private set; }
    public string? Exclusions { get; private set; }
    public string? Highlights { get; private set; }
    public string? TravellerRequirements { get; private set; }
    public string TimeZoneId { get; private set; } = Tour.DefaultTimeZoneId;
    public string DestinationSummary { get; private set; } = string.Empty;
    public long CancellationPolicyTemplateId { get; private set; }

    // Proposed child collections, stored as a JSON payload (locked decision, carried from rev. 2).
    // SchedulesJson entries MUST carry each existing TourSchedule.Id they replace, or null for a
    // newly proposed schedule, so the Approve-time BR-61 re-check can match proposed-to-existing.
    // A past-departure schedule (§3 point D) is always present with its original id and original
    // values, and is never subject to BR-22 re-validation.
    public string DestinationIdsJson { get; private set; } = "[]";
    public string ItineraryItemsJson { get; private set; } = "[]";
    public string SchedulesJson { get; private set; } = "[]";

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public long? ReviewedByUserId { get; private set; }
    public DateTimeOffset? ReviewedAtUtc { get; private set; }
    public string? RejectionReason { get; private set; }

    public static TourPendingChangeSet CreateDraft(
        long tourId, long submittedByUserId, /* initial field snapshot copied from live Tour,
        including every existing TourSchedule verbatim with its original id */
        DateTimeOffset nowUtc);

    public void SaveDraft(/* all proposed fields */, DateTimeOffset updatedAtUtc)
    {
        if (Status is not (TourPendingChangeStatus.Draft or TourPendingChangeStatus.Rejected))
            throw new InvalidOperationException("Only a Draft or Rejected version can be edited.");
        // field assignment + BR-61/MSG143, BR-22/MSG29 (future/new schedules only, §3 point D),
        // MSG01 validation, mirroring Tour.UpdateDraft's own checks
    }

    public void Submit(DateTimeOffset submittedAtUtc)
    {
        if (Status is not (TourPendingChangeStatus.Draft or TourPendingChangeStatus.Rejected))
            throw new InvalidOperationException("Only a Draft or Rejected version can be submitted.");
        // BR-101 completeness re-check happens in the handler before calling Submit
        Status = TourPendingChangeStatus.PendingReview;
        SubmittedAtUtc = submittedAtUtc.ToUniversalTime();
        RejectionReason = null;
    }

    public void MarkApplied(long reviewerUserId, DateTimeOffset reviewedAtUtc)
    {
        if (Status != TourPendingChangeStatus.PendingReview)
            throw new InvalidOperationException("Only a PendingReview version can be approved.");
        Status = TourPendingChangeStatus.Applied;
        ReviewedByUserId = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
    }

    public void Reject(long reviewerUserId, string reason, DateTimeOffset reviewedAtUtc)
    {
        if (Status != TourPendingChangeStatus.PendingReview)
            throw new InvalidOperationException("Only a PendingReview version can be rejected.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > Tour.RejectionReasonMaxLength)
            throw new ArgumentException("A valid rejection reason is required.", nameof(reason)); // BR-119
        Status = TourPendingChangeStatus.Rejected;
        RejectionReason = reason.Trim();
        ReviewedByUserId = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
    }
}
```

### 4.2 `Tour` entity changes

No changes to `TourStatus` enum or existing mutators (VERIFIED unnecessary — §2 Out of scope). One
new guarded domain method, unchanged from revision 2:

```csharp
// Tour.cs
public void ApplyPendingChange(
    int categoryId, string title, string description, decimal basePrice, int durationDays,
    string? inclusions, string? exclusions, string? highlights, string? travellerRequirements,
    string timeZoneId, IEnumerable<long> destinationIds, string destinationSummary,
    CancellationPolicyTemplate cancellationPolicy,
    IEnumerable<TourItineraryItem> itineraryItems, IEnumerable<TourSchedule> schedules,
    DateTimeOffset appliedAtUtc)
{
    if (Status is not (TourStatus.Approved or TourStatus.Published))
        throw new InvalidOperationException("Only Approved or Published tours accept a pending change.");
    // same field assignment as UpdateDraft, gated on Approved/Published, does NOT touch
    // Status/ReviewedBy/PublishedAtUtc (PC-02).
}
```

## 5. Proposed API contract

All endpoints reuse the existing `Result<T>`/`HandleFailure` pipeline and `[Authorize]` conventions.

### 5.1 Operator: create (or resume) a draft version from the live tour

`POST /api/v1/operator/tours/{tourId}/pending-changes`

Condition on the live tour's current `Status` (rewritten this revision for clarity, §3 point G4):

| Live `Tour.Status` | Result |
|---|---|
| `Draft` or `Rejected` | `409 operator_tour.invalid_state` — no draft version is created; the operator must use the existing UC-35 direct-edit `PUT` endpoint instead, which already handles these two statuses. |
| `Pending` | `409 operator_tour.invalid_state` + **MSG122** (currently pending approval). |
| `Inactive` | `409 operator_tour.invalid_state` (§3 point G2 — handling `Inactive` tours any more specifically is out of scope for TM-82). |
| `Approved` or `Published` | **Only this case creates or resumes a draft version.** Idempotent: an existing open (`Draft`/`PendingReview`/`Rejected`) draft version is returned (`200 OK`) instead of duplicated (enforced by the unique filtered index, §8). Otherwise creates a new `Draft`, seeded from the live tour's current values, with every existing `TourSchedule` carried over verbatim including past-departure ones (§3 point D), and returns it (`201 Created`). |

There is no longer an `operator_tour.pending_change_conflict` error (§3 point G3): because create
is idempotent, a second call while a draft version is already open is a success (`200 OK`, same
row), not a conflict.

### 5.2 Operator: view current open draft version

`GET /api/v1/operator/tours/{tourId}/pending-changes/current` → the open draft version, or `404`.

### 5.3 Operator: save draft changes

`PUT /api/v1/operator/tours/{tourId}/pending-changes/{pendingChangeId}`

Allowed only while `Draft` or `Rejected`; `PendingReview` → `409 operator_tour.invalid_state`
(MSG122). Validates ownership, BR-61/MSG143, BR-22/MSG29 (future/new schedules only — §3 point D),
MSG01.

### 5.4 Operator: discard the draft version

`DELETE /api/v1/operator/tours/{tourId}/pending-changes/{pendingChangeId}`

Allowed only while `Draft` or `Rejected`. Deletes the row outright. `204 No Content`.

### 5.5 Operator: submit the draft version for approval

`POST /api/v1/operator/tours/{tourId}/pending-changes/{pendingChangeId}/submit`

Allowed only while `Draft` or `Rejected`. Re-checks BR-101 completeness. On success:
`Status = PendingReview` (MSG111 analogue), enters the moderation queue (§5.6).

### 5.6 Admin: moderation queue — additive endpoint (§3 point E2)

**New** endpoint, e.g. `GET /api/v1/admin/tours/moderation-queue?page=1`, returning both first-time
`Pending` tours and `PendingReview` draft versions in one paginated list (CR-01), each row tagged
with a `kind` discriminator (`"NewSubmission"` or `"DraftVersion"`). **The existing Admin
tour-listing endpoint's contract is not changed by this spec** — if the plan phase determines that
approach does not work for the real Admin UI, that is a new decision point, not something this
spec pre-approves.

### 5.7 Admin: approve a draft version

`POST /api/v1/admin/tours/pending-changes/{pendingChangeId}/approve`

Authorization: `Administrator` (§1.3/§3 point A3 — the SRS's "Staff" is mapped here). Behavior:
1. Load the draft version; `404` if missing.
2. `409 operator_tour.invalid_state` (MSG133) unless `Status == PendingReview`.
3. Load the live `Tour`; defensive `409` unless still `Approved`/`Published`.
4. **BR-61 re-check**: proposed schedules must still honor every existing active booking on a
   matched schedule; past-departure schedules (§3 point D) are always carried over unchanged so
   they trivially pass. Failure → `operator_tour.schedule_has_active_bookings`, applies nothing,
   stays `PendingReview`.
5. `tour.ApplyPendingChange(...)`.
6. `changeSet.MarkApplied(adminUserId, now)`.
7. Audit log (`TourPendingChangeApproved`), before/after snapshot.
8. **Commit** steps 1-7 together as **one serializable transaction** (BR-130: the decision and its
   audit record belong together, §3 point G5). The call returns success (MSG144) as soon as this
   transaction commits.
9. **After** that commit, in a separate step, attempt to create an in-app `Notification` for
   `changeSet.SubmittedByUserId`. If this attempt fails, it does **not** reopen, reverse, or fail
   the already-committed decision (UC-60 9.a2, §3 point B/G5) — the exact retry/best-effort
   mechanism for this step is a plan-phase decision, not designed here.

### 5.8 Admin: reject a draft version

`POST /api/v1/admin/tours/pending-changes/{pendingChangeId}/reject`

Request: `{ "reason": "..." }`; empty → `400` **MSG123** (§3 point A1). Behavior:
1. Load, `409` MSG133 unless `PendingReview`.
2. `changeSet.Reject(adminUserId, reason, now)`.
3. Audit log (`TourPendingChangeRejected`).
4. **Commit** steps 1-3 together as one serializable transaction. The call returns success
   (**MSG145** — not MSG144, §3 point A1) as soon as this transaction commits. Live `Tour` is never
   touched by this call.
5. **After** that commit, attempt the in-app notification for the operator, same independence rule
   as §5.7 step 9 (UC-61 8.a2, §3 point B/G5).

## 6. Business rules

### 6.1 Ownership and state gates

- Only the tour's own `OperatorUserId` may create, save, discard, or submit its draft version
  (BR-102).
- At most one open (`Draft`/`PendingReview`/`Rejected`) draft version per tour, enforced by a
  unique filtered database index, not application logic alone (§3 point G6, §8).
- Save/discard/submit valid only while `Draft` or `Rejected`; read-only while `PendingReview`
  (UC-36 2.a2, UC-37 PC-03).
- Approve/Reject require the mapped `Administrator` role (§1.3/§3 point A3) and
  `Status == PendingReview` (BR-104).
- Every schedule id referenced inside a `TourPendingChangeSet`'s `SchedulesJson` payload must
  belong to the **same** `TourId` the change set is for; a schedule id from a different tour is
  rejected at save/submit time, not silently accepted (§3 point G6).

### 6.2 All-fields-require-review

Confirmed, §1.4/§3 point E1: no field-level carve-out (BR-103).

### 6.3 Live tour isolation

Confirmed Task-Owner reading, §3 point C: "Pending Approval" belongs to the draft version, not the
live tour. The live `Tour` row and its children are unmodified, and remain discoverable/bookable
exactly as before, for the entire time a draft version is outstanding (UC-36 PC-02).

### 6.4 BR-61 capacity guard, enforced three times

1. **Save draft**: rejects a proposed (non-past, §3 point D) schedule whose capacity is below its
   matched existing schedule's sold-slot count (MSG143).
2. **Submit**: re-validated as part of BR-101 completeness.
3. **Approve**: re-checked immediately before `ApplyPendingChange`, inside the same serializable
   transaction, against the live tour's current booking state.

### 6.5 Past-departure schedule immutability (new, §3 point D)

A `TourSchedule` whose departure date is in the past at draft-creation time is carried over
verbatim with its original id, is never subject to BR-22, and is never treated as removable by the
draft version (so it never triggers a BR-61 "removed schedule" failure either). BR-22 applies only
to schedules that are new or are future-dated and edited.

## 7. Error contract

| Condition | HTTP | Stable code | SRS message |
|---|---:|---|---|
| Not an active approved Tour Operator | 403 | `operator_tour.access_required` | MSG126 |
| Not the mapped Administrator role | 403 | `operator_tour.admin_access_required` | MSG126 |
| Tour not found / not owned | 404 | `operator_tour.not_found` | MSG126 (ownership) |
| Draft version not found | 404 | `operator_tour.pending_change_not_found` | — |
| Direct-edit `PUT` attempted on an Approved/Published tour (existing UC-35 endpoint, §2 new in-scope item) | 409 | `operator_tour.invalid_state` | **MSG110** (§3 point A2) |
| Create attempted while the tour itself is `Draft`/`Rejected` | 409 | `operator_tour.invalid_state` | — (use the existing UC-35 `PUT` endpoint instead, §5.1/G4) |
| Create/save/submit attempted while the tour itself is `Pending` | 409 | `operator_tour.invalid_state` | **MSG122** (§3 point A2) |
| Create attempted while the tour itself is `Inactive` | 409 | `operator_tour.invalid_state` | — (§3 point G2, out of scope beyond this rejection) |
| Save/discard/submit attempted while the draft version is `PendingReview` | 409 | `operator_tour.invalid_state` | MSG122 (read-only) |
| A schedule id in the payload does not belong to this tour | 400 | `ValidationProblemDetails` | — (§3 point G6) |
| Mandatory field empty (save) | 400 | `ValidationProblemDetails` | MSG01 |
| Completeness checklist incomplete (submit) | 400 | `ValidationProblemDetails` | MSG01 |
| Capacity reduced below sold slots (save/submit/approve) | 409 | `operator_tour.schedule_has_active_bookings` | MSG143 |
| Invalid departure/return date (new or future-edited schedule only, §6.5) | 400 | `ValidationProblemDetails` | MSG29 |
| Draft version not `PendingReview` on approve/reject | 409 | `operator_tour.invalid_state` | **MSG133** (§3 point A1 — shared by approve and reject) |
| Rejection reason missing/too long | 400 | `operator_tour.rejection_reason_invalid` | **MSG123** (§3 point A1, corrected) |
| Unexpected persistence/network failure | 500 | existing ProblemDetails pipeline | MSG127 |
| Notification delivery fails after a decision is stored | — | not an error response; decision already returned success | queued for retry (§3 point B/G5); never surfaces as a failure of the approve/reject call |
| **Approve succeeds** | 200 | — | **MSG144** |
| **Reject succeeds** | 200 | — | **MSG145** (corrected — was miscited as MSG144 before revision 3) |
| Save/update succeeds | 200 | — | MSG109 |
| Submit succeeds | 201/200 | — | MSG111 |

**Removed this revision (§3 point G3):** `operator_tour.pending_change_conflict` no longer exists.
Create is idempotent (§5.1), so a second create call while a draft version is already open is a
`200 OK` success, not a conflict error.

## 8. Data and architecture

- New table `commerce.TourPendingChangeSets`, migration script
  `database/migrations/<date>_tour_pending_change_sets.sql`, guarded `IF COL_LENGTH`/`IF NOT EXISTS`
  + header-comment format (see `20261015_add_tour_package_authoring.sql` and
  `20261124_tour_recommendation_attributes.sql`). `status` CHECK:
  `('Draft','PendingReview','Applied','Rejected')` — no `'Cancelled'` value.
- **New unique filtered index (§3 point G6)**: at most one row per `tour_id` where
  `status IN ('Draft','PendingReview','Rejected')` — e.g.
  `CREATE UNIQUE INDEX UX_TourPendingChangeSets_OpenPerTour ON commerce.TourPendingChangeSets(tour_id) WHERE status IN ('Draft','PendingReview','Rejected')`,
  guarded the same idempotent way as the rest of the migration script. This enforces the
  single-open-draft-version rule at the database level, not only in the command handler.
- **Schedule-id ownership validation (§3 point G6)**: before a `SaveDraft`/`Submit` call accepts a
  `SchedulesJson` payload, every non-null schedule id in it must resolve to a `TourSchedule` whose
  `TourId` equals this change set's own `TourId`; otherwise the request is rejected as a validation
  error (§7). This is an application-level check (no new FK constraint on a JSON column is
  possible), run in the same handler that validates BR-61/BR-22/MSG01.
- New `IEntityTypeConfiguration<TourPendingChangeSet>`; `DateTimeOffset(?)` columns use
  `.AsUtcDateTime2()`; `Status` via `HasConversion<string>()`.
- New `DbSet<TourPendingChangeSet> TourPendingChangeSets` on `ApplicationDbContext`/
  `IApplicationDbContext`.
- No EF migrations; no generic repository.
- New `AuditActionTypes` consts: `TourPendingChangeCreated`, `TourPendingChangeSaved`,
  `TourPendingChangeSubmitted`, `TourPendingChangeApproved`, `TourPendingChangeRejected`,
  `TourPendingChangeDiscarded`.
- New `OperatorTourErrorCodes` consts per §7.
- A notification mechanism for the operator on approve/reject, built on the existing `Notification`
  entity, attempted **after** the decision transaction commits (§3 point G5) — the plan phase picks
  the smallest mechanism satisfying this and must stop and ask the Task Owner before adding any new
  infrastructure (outbox table, background worker, queue) if it concludes one is required.
- New additive Admin moderation-queue read endpoint (§5.6); the existing Admin tour-listing
  endpoint's contract is not changed by this spec.
- Reuses `ExecuteInSerializableTransactionAsync` for save/submit, and for the approve/reject
  decision-plus-audit-log transaction specifically (§5.7-5.8) — the notification step is
  deliberately outside that transaction (§3 point G5).

## 9. Acceptance criteria

1. An operator can create a draft version only for a tour they own that is `Approved` or
   `Published`; a second create call while one is open returns the existing draft version
   (`200 OK`, idempotent — never `operator_tour.pending_change_conflict`, which no longer exists).
   Attempting to create one for a `Draft`/`Rejected` tour, a `Pending` tour, or an `Inactive` tour
   each return `operator_tour.invalid_state` (with MSG122 for `Pending`).
2. Edit (`PUT`) and discard (`DELETE`) are rejected with `operator_tour.invalid_state` while
   `PendingReview`.
2a. A unique filtered database index prevents a second open (`Draft`/`PendingReview`/`Rejected`)
    `TourPendingChangeSet` row for the same tour from ever being persisted, even under concurrent
    requests — not just rejected by application logic.
2b. A `SchedulesJson` payload referencing a schedule id that belongs to a different tour is
    rejected as a validation error at save/submit time.
3. Submitting requires BR-101 completeness and lands in the moderation queue (§5.6), tagged with
   the correct `kind`.
4. Only the mapped Administrator role can approve or reject; any other status is rejected with
   MSG133.
5. Approving applies every proposed field and writes one audit log row **together in one committed
   transaction**, leaves `Tour.Status` unchanged, and returns success (MSG144) as soon as that
   transaction commits. The in-app notification is attempted only **after** that commit, and its
   failure never reopens, reverses, or fails the already-returned decision (§3 point B/G5).
6. Rejecting requires a non-empty reason (else MSG123), never touches the live tour, commits its
   decision and audit log row together, returns MSG145 on success — **not** MSG144 — and only then
   attempts the in-app notification, with the same failure-independence guarantee as approve.
7. The BR-61 guard rejects an under-capacity *non-past* schedule at save, submit, and approve.
8. **A `TourSchedule` whose departure date is already in the past when the draft version is
   created is carried over with its original id and values, is never rejected for an "invalid
   departure date" (BR-22 does not apply to it), and can never be the cause of a BR-61 "removed
   schedule" failure.**
9. Creating, saving, discarding, or submitting a draft version never modifies the live tour (until
   Approve).
10. The existing direct `Draft`/`Rejected` tour edit path is unaffected; calling the existing
    direct-edit `PUT` endpoint against an `Approved`/`Published` tour instead returns MSG110, not a
    generic error.
11. There is no `Cancelled` state anywhere.
12. The existing Admin tour-listing endpoint's response contract is unchanged by this feature; the
    combined queue is served by a new, separate endpoint.
13. Unit and integration tests cover: ownership, every state gate, the single-open-draft-version
    rule, read-only-while-`PendingReview`, atomic apply, the BR-61 guard at all three checkpoints
    (including the exactly-at-capacity boundary), the past-departure-schedule immutability rule
    (including a tour whose only existing schedule is already past), MSG144-vs-MSG145 on the two
    outcomes, MSG123 on an empty rejection reason, MSG133 on a stale-decision attempt for both
    approve and reject, and that `Tour.Status` never changes on approve.

## 10. Open items — all closed

**No open items remain.** The one item still open in revision 3 is now resolved:

1. **Open item 1 — version-mechanism identity. CLOSED by the Task Owner on 2026-11-25 (§3 point
   G1): the lightweight `TourPendingChangeSet` entity is used; `VersionRootTourId`/`VersionNo` is
   not reused.** The analysis below is retained verbatim as the record of what that decision was
   weighed against.

   **(a) Where `VersionRootTourId`/`VersionNo` is used today, and what happens on approval —
   VERIFIED, read-only code inspection:**
   - Both fields live on `Tour` (`Tour.cs:35,37`), set once via `SetVersionLineage(...)`
     (`Tour.cs:169-176`), which throws if lineage is already set (immutable once assigned).
   - The **only** place that sets lineage today is `CreateTourPackageCommandHandler.cs:96-114`, when
     the request carries a `SourceTourId`. **VERIFIED**: this path requires the source tour to be
     `Approved` or `Published` (`CreateTourPackageCommandHandler.cs:106-108`), then creates a
     **brand-new `Tour` row** (a new identity/primary key) with
     `versionRootTourId = source.VersionRootTourId ?? source.Id` and `versionNo` computed as
     `max(existing versionNo for that root) + 1`.
   - **VERIFIED**: `TourReviewCommandHandlers.cs` (Approve/Reject/Publish/Unpublish) never
     references `VersionRootTourId`/`VersionNo` at all — approving a "new version" tour only
     changes that one row's own `Status`; there is no step anywhere that archives, unpublishes, or
     otherwise touches the earlier version(s) sharing the same `VersionRootTourId`.
   - **VERIFIED**: no query outside the Operator's own Get/List/Create/Delete handlers for Tours
     references `VersionRootTourId`/`VersionNo` — in particular, `TourVisibility.cs` (shared
     visibility rule used by public search per `SearchToursQueryHandler.cs`) and
     `CouponTourEligibility.cs` filter purely on `Tour.Status`/`PublishedAtUtc`, with no lineage
     awareness.
   - **Consequence, VERIFIED by the above findings taken together:** if an operator today creates a
     new version from a `Published` tour and that new version is later `Approved`→`Published`,
     **both the old and the new `Tour` rows end up `Published` simultaneously**, both independently
     visible to public search and both independently bookable, with nothing in the current code
     that supersedes or hides the older one. This is an existing, pre-TM-82 gap in that mechanism
     (not something TM-82 introduces), surfaced here only because Open item 1 asks whether TM-82
     should build on it.

   **(b) If the draft-version mechanism in this spec instead reused `VersionRootTourId`/`VersionNo`
   (i.e. "new draft version" = a new full `Tour` row, not a lightweight `TourPendingChangeSet`) —
   impact on `tour_id`/`schedule_id` identity and on Booking/Review/Coupon/Search, VERIFIED/NOT_VERIFIED
   per claim:**
   - **VERIFIED**: `Booking.TourScheduleId` (`Booking.cs:36`) points at a specific `TourSchedule`,
     which in turn belongs to a specific `Tour.Id` — a new `Tour` row would need entirely new
     `TourSchedule` rows (new ids), so **every existing `Booking` would keep pointing at the old
     tour's old schedule ids**, which is actually *correct* for bookings already made against the
     old version — but it means the new version's own schedules start with **zero** bookings of
     their own, so this spec's BR-61 "capacity vs. sold slots" re-check (§6.4) would trivially pass
     against the new version's empty schedules, while the real sold-slot history lives on the old
     version's now-orphaned-from-review schedules. **This is a meaningful semantic difference from
     the `TourPendingChangeSet` approach**, where the proposed schedules are matched back to the
     *same* `TourSchedule.Id`s the live tour already has, so BR-61 is checked against the real
     booking history on those exact rows.
   - **VERIFIED**: `Review.TargetId` (`Review.cs`, `TargetTypeTour = "Tour"`) is a fixed `Tour.Id`.
     A new `Tour` row for the "same" logical tour would start with **zero reviews**, and existing
     reviews would stay attached to the old `Tour.Id` — splitting a tour's review history across
     multiple row identities for what a Traveler perceives as "one tour." The
     `TourPendingChangeSet` approach has no such effect, because `Tour.Id` never changes.
   - **VERIFIED**: `CouponTourEligibility.IsEligible` checks only `Status`/`PublishedAtUtc` on
     whichever `Tour` row is passed to it — a coupon scoped to the old tour's id (if coupons are
     ever tour-scoped; **NOT_VERIFIED** whether any coupon feature currently scopes to a specific
     `TourId` at all, since `grep` found no `TourId` reference inside the `Coupons` feature folder
     during this check) would not automatically apply to a new version's row. This point is
     **NOT_VERIFIED as a real current risk** (no evidence coupons are tour-scoped today) but
     flagged because it would need re-checking if that changes.
   - **VERIFIED**: public search (`SearchToursQueryHandler.cs`, via `TourVisibility.cs`) has no
     lineage deduplication — per (a) above, two `Published` rows from the same lineage would both
     appear as separate search results, a real, already-existing defect this spec would inherit
     and worsen in frequency if it builds the new feature on this mechanism (today it can only
     happen via explicit `SourceTourId` creation, which is one specific operator action; TM-82
     would make it happen on every approved edit to a live tour, via a different code path).

   **(c) Comparison and recommendation (recommendation only — this spec does not decide):**
   - **`TourPendingChangeSet` (this spec's current approach):** `Tour.Id` never changes; Bookings,
     Reviews, and any Coupon scoping keep working against the one stable id with no special-casing;
     BR-61's capacity check runs against the real existing `TourSchedule` rows and their real
     booking history; search/visibility needs no dedup logic because there is still exactly one
     `Tour` row. Cost: a new table and new domain entity, and the field-assignment logic is
     duplicated (or refactored into shared helpers) between `UpdateDraft` and `ApplyPendingChange`
     (§4.2).
   - **Reusing `VersionRootTourId`/`VersionNo` (a new full `Tour` row per draft version):** reuses
     an existing mechanism and its existing `Create`/`Get`/`List` plumbing, but (per (b) above)
     would require **new, currently-nonexistent work** to: (i) make BR-61's capacity check look at
     the *correct* schedule rows (likely still the old version's, which defeats the point of a
     "new" row for schedules), (ii) decide what happens to Reviews and any tour-scoped Coupons when
     the "canonical" row for a tour changes identity, and (iii) add the dedup/supersession logic
     that is already missing today (per (a)) so an approved new version actually replaces the old
     one in search instead of running alongside it. None of this is designed anywhere in the
     current codebase; it would all be new scope.
   - **Recommendation (not a decision): the `TourPendingChangeSet` approach is the lower-risk,
     lower-new-scope option**, because it does not inherit or amplify the existing
     lineage/supersession gap described in (a), and keeps every booking/review/coupon reference
     stable. This is offered as this spec's considered recommendation; the Task Owner's explicit
     instruction was not to decide this unilaterally, so §4 continues to specify the
     `TourPendingChangeSet` design pending that sign-off, and this analysis is what the sign-off
     decision should be weighed against.

**Cleared to proceed to the plan and implementation phases (AGENTS.md §1.2 steps 2-4) — see the
Task Owner's approval record, §3 point G.**

## 11. Changelog versus revision 3 (this revision)

- **G1.** Closed the last open item: the Task Owner chose `TourPendingChangeSet` over reusing
  `VersionRootTourId`/`VersionNo`. §10 rewritten from "open" to "closed, decision recorded."
- **G2.** Added the `Inactive`-tour rule: `operator_tour.invalid_state`, no dedicated flow, out of
  scope beyond that rejection.
- **G3.** Removed `operator_tour.pending_change_conflict` — create was already idempotent, so there
  is no conflict case to report (§5.1, §7).
- **G4.** Rewrote §5.1's precondition as an explicit per-status table for clarity.
- **G5.** Locked the decision-then-notify transaction shape for Approve/Reject (§5.7, §5.8, §9):
  decision + audit log commit together in one serializable transaction; notification is attempted
  only after that commit, and its failure never reopens the decision. The *mechanics* of the retry
  step itself remain a plan-phase decision (§3 point B), with an explicit instruction to stop and
  ask before adding new infrastructure.
- **G6.** Added two data-integrity requirements: a unique filtered index enforcing the
  single-open-draft-version rule at the database level, and a schedule-id ownership check rejecting
  a `SchedulesJson` entry that belongs to a different tour. Added to §6.1, §8, §9.
- Added §0's note that the existing `SourceTourId`/version-lineage path is untouched by TM-82, and
  its pre-existing two-Published-rows gap is a separate, already-existing defect for its own ticket
  (§2 Out of scope).
- Added §2's new in-scope item: the existing UC-35 direct-edit `PUT` endpoint gets a small additive
  change (MSG110 on Approved/Published), with its Draft/Rejected behavior otherwise unchanged.
- Recorded the Task Owner's approval of revision 3 plus these six corrections, and the explicit
  authorization to proceed into `plans/TM-82-plan.md` and implementation (§3 point G, title block).

## 12. Changelog versus revision 2 (carried forward from revision 3)

- **A1.** Corrected UC-61's success code from a mistaken MSG144 to **MSG145**; added **MSG123**
  (empty rejection reason) and confirmed **MSG133** is shared by both approve and reject on a
  stale-status decision attempt. All three VERIFIED by direct quotation in both SRS files (§1.2).
- **A2.** Corrected MSG110's meaning: it is UC-36's "tour is Approved, offered a new draft version"
  message, not a generic invalid-state code. Reassigned the error contract (§7) so a direct edit of
  an Approved/Published tour via the existing UC-35 endpoint returns MSG110, a `Pending` tour
  returns MSG122, and draft-version state errors return MSG122/MSG133 as appropriate.
- **A3.** Investigated and recorded the PDF/DOCX role disagreement in full (§1.3) — the PDF says
  "Administrator" and the newer DOCX says "Staff" for UC-60/UC-61, with the DOCX itself internally
  inconsistent in one spot (UC-60's own Validation table still says "Administrator"). Documented as
  a deliberate implementation mapping ("Staff" → existing `UserRole.Administrator`), not silently
  resolved as if the SRS agreed with itself.
- **B.** Added the explicit requirement that a notification-delivery failure must never roll back a
  stored Approve/Reject decision (UC-60 9.a2 / UC-61 8.a2), analyzed whether
  `AdministratorInAppNotices.AddAsync` already satisfies this (**it does not** — it shares one
  transaction with the decision today, VERIFIED), and left the actual decoupling mechanism as an
  explicit open design task for the plan phase, per instruction not to design an outbox here.
- **C.** Marked the "Pending Approval belongs to the draft version, not the live tour" reading
  (first written in revision 2's §6.3) as now Task-Owner-confirmed, with its own section explaining
  the UC-37/BR-56-vs-UC-36-PC-02 tension it resolves.
- **D.** Added the past-departure-schedule immutability rule — new this revision — closing a
  BR-22/BR-61 deadlock for a completed tour that revision 2 did not address. Added to §3, §4.1,
  §6.5, §7, and acceptance criterion 8.
- **E1/E2/E3.** Closed three of revision 2's four open items: Appendix BR-96 vs. body-text BR-103
  (body text wins); the Admin moderation queue (one logical queue via a new additive endpoint,
  explicitly not touching the existing endpoint's contract); and the existing-handler notification
  gap (confirmed out of scope for TM-82, tracked separately).
- **Open item 1 only remains.** Added a full, code-grounded impact analysis (§10) comparing the
  `TourPendingChangeSet` entity against reusing `VersionRootTourId`/`VersionNo`, with every claim
  marked VERIFIED or NOT_VERIFIED, and a recommendation offered without deciding the question.
- **F.** Restated, unchanged from revision 2: withdrawing/deleting a `PendingReview` version is not
  found in the SRS and stays out of scope.

## 13. Changelog versus revision 4 (this revision)

- **H.D1.** Closed the notification-retry mechanics question that §3 point B/G5 had left for the
  plan phase: there is no retry for TM-82. A failed post-commit notification attempt is logged at
  Error level (with the change-set id and acting user id) and not retried; this is recorded as a
  deliberate, known divergence from UC-60 9.a2/UC-61 8.a2's literal "queued for a retry" wording,
  not a reading of the SRS. Narrows (does not remove) Task 7 step 4's stop condition to
  infrastructure needs other than "no retry, log and move on."
- **H.D2.** Confirmed and recorded as an explicit Out of scope limitation: draft versions carry no
  media; an Approved/Published tour's photos stay uneditable until a future ticket. Recorded the
  Task 0 code check this revision performed (VERIFIED): every tour-media-mutating handler
  (`Upload`/`Delete`/`Reorder`/`UpdateMetadata`) already restricts itself to `Draft`/`Rejected`
  tours via `TourMediaAccessResolver.AllowsMaterialChange`/the equivalent inline guard — no BR-103
  bypass exists through the media endpoints, so no code change was needed or made for this check.
- **H.D3.** Clarified that Task 6's BR-101 completeness check for a draft version uses only the
  three SRS-named conditions (mandatory fields, ≥1 itinerary day, ≥1 departure schedule), not
  `TourReviewReadinessEvaluator`'s six facts — specifically excluding that evaluator's
  `PrimaryImageMissing` fact, which does not apply per H.D2 (a draft version has no media to check).
  Task 6 must not import or re-derive a media check for the draft version from that evaluator.
- No changes to §3 point B/G5's locked transaction shape (decision-then-notify), to the domain
  model (§4), to the API contract (§5), to the error contract (§7), or to the data/architecture
  section (§8) beyond what H.D1-H.D3 state above.
