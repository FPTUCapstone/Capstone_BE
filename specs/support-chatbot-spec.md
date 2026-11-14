# Support chatbot

## Readable user-facing answers

The user requests concise replies without visible BR/CR/UC identifiers. Preserve internal source IDs for validation while returning human-readable SRS source titles. Default to short plain-text case headings and bullet lines, avoiding uppercase English actors, implementation jargon and repeated introductions. Preserve financial conditions and exceptions. Web renders paragraphs, headings and lists safely as React text. Mobile receives the same plain-text outline without Markdown markers.

## Complete SRS business-rule knowledge

The user authorizes importing all current business rules from Report 3, file 1VwPkqRyC6ZlbXBQC0ztdektt0vAyE3qt, modified 2026-11-12. Preserve the 186 active BR definitions, 15 CR definitions, section-specific details and four retirement notices in a versioned local snapshot. Group bounded retrieval passages by functional section with rule IDs, source URL and revision. Retired rules cannot be presented as current policies. Include cross-rule cancellation/refund context so broad policy questions are answered without requiring an existing booking. A specific booking amount continues to require authorized runtime data and its stored policy version. Source-document changes require a reviewed snapshot update. Application implementation remains separate from documented requirements.

## Approved manual-test corrections

- Preserve adult, child and infant counts separately through planner, capacity checks, selection prompts and booking URLs. The existing partySize field carries adults, with optional childCount and infantCount. Validate the total assisted prefill at 1–50 participants.
- Require enough places for every participant. Mark insufficient and unknown capacity as ineligible for the requested party and provide explicit clarification when no departure fits.
- Present supplied local departure times in Vietnam GMT+7. Application facts remain authoritative.
- Web replies continue while users operate or navigate the public site. Account changes, unmount, explicit context replacement and Clear chat still cancel pending requests. Response completion preserves focus on the user's current web control.

The approved direction uses an authenticated Web/Mobile chatbot, Gemini API, retrieved project knowledge and existing authorized application queries, with independent SupportChat configuration.

POST /api/v1/support-chat/messages accepts message (1–2000 chars), up to ten history messages (user/assistant, 1–2000 chars each), and optional context { kind: refund|itinerary|tour, id: positive Int64 represented as a string }. Traveler and TourOperator roles only. Response { answer, sources: [{id,title,url}], context?, actions? }. Error contract is ProblemDetails, with 400, 401, 403, 404, 429 and 503.

The server retrieves curated documentation and validates cited IDs against retrieved sources. Resource access uses existing public tour and authorized personal-data queries before sending minimal context to Gemini. Tools read current application data and prepare navigation through existing booking flows. Conversation history is transient. Cancellation and refunds remain explicit application actions. Unsupported questions receive a grounded no-information response. Personal data is never added to the knowledge index. Knowledge and model configuration are isolated from scheduling AI.

Verification includes request validation, authorization before model calls, retrieval relevance, citation validation, provider failure/timeout, bounded request cost, frontend accessibility and stale requests.

Follow-up retrieval uses the entire current message followed by up to three recent user turns, bounded to 4,000 UTF-16 code units while preserving complete surrogate pairs. Conversation history supplies topic context and remains untrusted. Factual answers require retrieved documentation or authorized resource data.

## Unified assistant expansion approved by the user

Use a single conversation with implicit page context and explicit selection only when several records match. A bounded Gemini query planner may select approved read-only capabilities for public tour search/detail, a Traveler's bookings/itineraries and existing authorized refund/itinerary detail. Application queries remain authoritative for current prices, availability and ownership. Newly published or updated data is read at question time.

Responses may include optional server-created actions with type, label, URL, context, prompt and description. Navigation uses existing internal routes. Booking preparation links to the existing tour booking form with a selected schedule and an adult-count suggestion. The form retains participant, price, availability and final-confirmation validation before creating a booking or entering payment. The assistant's tools execute read operations.

Assisted adult-count prefill is bounded to 1–50 to limit generated form state. The existing checkout remains authoritative for supported party composition and schedule capacity. A selected adult-only handoff initializes child and infant counts to zero. Larger parties can use the normal booking form. Multiple matching tours or departures require a user selection. A unique public tour match may be expanded directly to its live detail and departure choices.

Web has one scrollable message area, a fixed composer and automatic context from existing page routes or explicit resource actions. Conversation state remains transient within the signed-in session. Closing and reopening the panel retains messages. Clear chat explicitly clears the current conversation. Persisted multi-conversation history is outside this change.

## Mobile greeting and layout follow-up

The user approved natural greetings and a more consistent Android chat layout.
Pure greetings and short thanks use bounded social replies after authentication,
validation and resource authorization. Mixed greeting/business questions continue
through the normal retrieval and live-data flow.

Mobile uses aligned message bubbles, readable line spacing and padded full-width
actions. Adjacent matching tour-selection and tour-detail actions share one tour
card, with separate Ask about tour and View tour controls. Sources expand on demand.
The existing global conversation, safe internal navigation and booking validation
remain the interaction contract.
