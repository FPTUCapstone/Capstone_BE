# Railway contract PDF rendering

## Scope
Repair deployment support for the existing DOCX-to-PDF preview and signing renderer. No changes to contract approval rules, payment routing, database schema or production data. Work on `fix/linhnv-booking-and-banking`.

## Findings
The API Dockerfile does not install LibreOffice or Poppler. The optional renderer Dockerfile does, but is not used by the standard API deployment. Local rendering uses a Docker adapter with per-job network and resource isolation. The executable adapter fails closed unless isolated execution is verified. Private artifact storage has been configured separately by the operator.

## Acceptance criteria
- Production rendering has LibreOffice, Poppler and Vietnamese-capable fonts.
- Document execution is isolated from outbound networking and application secrets; runtime, memory and concurrency are bounded. Do not mount a Docker daemon socket into the API or enable IsolatedExecutionVerified without evidence.
- Choose a Railway-compatible execution model only after a deployment feasibility check; if the current host cannot enforce isolation, use a dedicated isolated render worker rather than silently weakening the guard.
- Existing document inspection, PDF signature-anchor validation, private storage and authorization remain enforced.
- A real DOCX smoke test produces a valid PDF with Vietnamese text and a signature anchor in the deployment image. Negative tests cover unavailable renderer and timeout.
- Document exact environment variables, service/volume requirements and retry procedure for existing queued or failed previews.
- No remote push, production deployment or config mutation is included without a separate user instruction.

## Approval
Approved by the user on 2026-11-28 before implementation, together with the plan.

## Selected model
Use a dedicated authenticated private renderer service, not a Docker daemon socket inside the API. The standard API image remains unchanged. Per-document Linux restrictions fail closed on unsupported hosts. Actual Railway host verification is a deployment prerequisite and is not inferred from local Docker results.
