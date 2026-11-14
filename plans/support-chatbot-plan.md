# Support chatbot implementation

## Readable-answer follow-up

- User-facing answers use concise plain-text cases, short headings and bullets, natural role names and human-readable citation titles. Internal source IDs and full rule grounding remain available for validation.
- A failing source-label regression preceded response-title cleanup. Generation tests verify the concise-format instruction alongside constrained citation IDs. Focused Backend validation passed 177 tests with zero skipped.
- A real authenticated browser request returned a shorter refund-policy answer without visible rule codes. The Web renderer formats both separate headings and inline bullet case labels safely. Mobile retains compatible plain-text answers.

## Complete business-rule corpus plan

1. Extract the supplied SRS section 5.1 rule inventory and retain scoped definitions from section 3. Check distinct IDs and retired entries.
2. Add failing corpus coverage, provenance, retirement, refund-policy and explicit-rule retrieval tests.
3. Embed the versioned JSON snapshot as a resource, build bounded topic passages, and combine semantic retrieval with explicit rule references. Embed documents in bounded batches and publish the cache only after every batch validates.
4. Verify Backend tests and formatting, restart the local API, then exercise real policy questions across refunds, booking, authentication, group privacy, coupons and offline operation. Report any documented-policy/runtime discrepancy accurately.

### Complete business-rule corpus evidence

- Imported all 190 BR entries, including 186 current rules and four retirement notices, plus 15 CR rules. Preserved 394 scoped Section 3 rule definitions in the embedded 2026-11-12 snapshot. The resulting 66 SRS passages complement ten existing workflow passages.
- Corpus tests verify complete distinct rule coverage, source revision, bounded chunks, scoped role rules, current refund tiers, retirement notices and actor-specific refund exceptions. Search indexing uses compact canonical text while full scoped definitions remain available for grounding.
- Real embedding verification found that 16 full-dimension vectors exceeded the shared one-megabyte response limit. Batches now contain at most eight. Compact index text resolved the free-tier 30,000-token-per-minute cold-initialization limit encountered with duplicated source sections.
- Explicit BR/CR questions route to documentation, avoiding misinterpretation as booking IDs. Answer schemas constrain citations to the retrieved passage IDs. HTTP 503 receives one deadline-bound retry; metadata-only diagnostics omit credentials, provider bodies and user text.
- Focused solution-wide SupportChat validation passed 176 tests with zero skipped, comprising 12 API, 84 Application and 80 Infrastructure tests.
- The full Infrastructure suite passed 1,202 tests with three external Cloudinary smoke tests skipped. Final scoped formatting verification and `git diff --check` passed.
- Real authenticated Gemini checks covered general refund policy, cancellation 12 days before departure, ordinary operator cancellation, weather cancellation, retired BR-157, offline payment, opt-in group location, expired coupons and review editing after ten days. Manual checks caught and corrected conflation of Traveler refund tiers with operator cancellation, and of ordinary cancellation with immediate weather refunds. Final general-policy and operator-cancellation checks returned HTTP 200 with the correct distinctions and SRS citations.
- Some initial provider calls returned transient HTTP 503. The final local API remains running with its initialized knowledge index. Future SRS changes require a reviewed snapshot refresh.

## Manual-test follow-up

1. Reproduce lost child count and insufficient group capacity with reader/planner regression tests.
2. Preserve adult, child and infant counts through planning, prompt selection and safe form links, checking total capacity. Supply local departure labels and eligibility evidence to answer generation.
3. Verify the new local provider key, then test the real two-adult, mixed-party and 25-person follow-ups against SQL-backed tour 101.

The follow-up focused Backend run passed 166 tests, comprising 12 API, 84 Application and 70 Infrastructure tests. Real Gemini/SQL checks preserved two adults plus one child and rejected the 25-person request against 20 remaining places. Local provider configuration was updated without adding credentials to source control.

1. Define request/result and application ports, add validation/handler tests first.
2. Implement authorized context lookup using existing queries, retrieve relevant knowledge and validate citations.
3. Implement isolated Gemini generation and embedding adapters, curated versioned knowledge and bounded cached retrieval. Test HTTP contracts and failure modes with fake transport.
4. Add authenticated rate-limited endpoint and independent configuration. Run format, build, focused and solution tests.
5. Integrate Web and Mobile in isolated branches using the same JSON contract. Validate interaction and error handling.

API model credentials are supplied by environment or user secrets. Live provider verification requires valid credentials. Each validation result will be reported separately.

## Validation results

- Solution restore and build passed. Existing scheduling nullable and transitive package vulnerability warnings remain on the develop baseline.
- All 86 focused chatbot tests passed, comprising 10 API, 19 Application and 57 Infrastructure tests.
- Full API integration run passed 838 tests and skipped 618 environment-dependent tests. The focused API tests were rerun successfully after the final implementation changes.
- Full Application suite passed 4,685 tests with zero skipped.
- Full Infrastructure suite passed 1,179 tests and skipped three external smoke tests.
- Candidate replay suite passed all 13 tests.
- Scoped `dotnet format --verify-no-changes` and `git diff --check` passed.
- Scheduling benchmark reports regenerated by the full suite were restored to their original content and encoding.

The initial corpus contains ten versioned workflow passages. Live Gemini response quality, retrieval relevance and authenticated end-to-end operation require configured SupportChat credentials and a generation model available to that account. Live provider verification is pending.

## Review corrections

- Added history-aware retrieval and Unicode-safe query bounds with regression tests.
- Reran solution-wide SupportChat tests after the corrections. All 97 passed, comprising 10 API, 30 Application and 57 Infrastructure tests, with zero skipped.
- Scoped format validation and `git diff --check` passed. Existing package vulnerability and scheduling nullable warnings remain.

## Unified assistant execution

1. Define typed query-planning and live-data ports and optional server-controlled actions.
2. Add regression-first Gemini planner tests, validate its bounded intent and arguments, register it independently of scheduling AI.
3. Execute approved existing CQRS queries with authenticated identity, build minimal current facts and safe navigation/record-selection actions. Verify authorization, ambiguous/no matches, booking handoff and absence of mutations.
4. Integrate handler planning with existing context authorization, RAG grounding and citation validation. Extend tests and run relevant Backend suites.
5. Update Web single-scroll UI, hidden route context, safe actions and booking-count prefill with tests and browser checks. Keep Mobile response handling compatible and extend actionable support where existing routes permit.
6. Re-run validation, self-review and restart the authorized local test servers. Report scope limits and outstanding database/provider issues accurately.

### Unified assistant validation

- Solution-wide focused SupportChat validation passed 159 tests with zero skipped, comprising 12 API, 78 Application and 69 Infrastructure tests.
- Live Gemini generation and embedding requests succeeded using local User Secrets. Real authenticated requests covered tour search, selected-tour preparation and personal itinerary listing.
- The user approved closing local legacy groups 5 and 7 after confirming both had no Active members. The database was backed up before the change and all canonical migrations completed successfully.
- Current local catalog tours have no upcoming departures. Live booking navigation remains data-dependent. Regression tests cover schedule selection, capacity and adult-count handoff without creating a payment through chat.
- Existing dependency vulnerability warnings and the scheduling nullable warning remain in the baseline.

### Greeting and mobile-layout verification

- Application SupportChat regression suite passed 92 tests, including social
  greetings, mixed business questions and forbidden resource context during a greeting.
- API SupportChat suite passed 12 tests with zero skipped.
- A real authenticated request to the running local API returned the friendly
  Vietnamese greeting for hi with an empty source list.
- Mobile SupportChat regression suite passed 63 tests. Full Flutter analysis and
  focused Dart analysis passed with zero issues.
- Existing dependency vulnerability and unrelated scheduling nullable warnings
  remain present in Backend build output.
