# UC-09 Update Travel Preferences — Backend Specification

Status: Draft v4 — 2026-10-13

## Scope and decisions

An authenticated active Traveler can read and replace five optional profile preferences: interests, preferred transport mode, travel pace, food preferences, and risk tolerance.

`travelStyle`, `budgetLevel`, `autoApplyToPlans`, and `defaultBudgetVnd` are out of scope and are not persisted or returned by UC-09. Budget is selected optionally by the Traveler on each UC-10 scheduling request; it is not a profile default or a server-side scheduling fallback.

Preference option keys are canonical English values. Interest options are loaded from `catalog.POICategories` and `catalog.Tags`; clients localize labels themselves.

## API contract

All endpoints require an active user with role `Traveler`:

- `GET /api/v1/traveler/preferences/options`
- `GET /api/v1/traveler/preferences`
- `PUT /api/v1/traveler/preferences`

`GET` and `PUT` return:

```json
{
  "interests": ["Beach"],
  "preferredTransportMode": "Motorbike",
  "travelPace": "Moderate",
  "riskTolerance": "Medium",
  "foodPreferences": ["Vegan"],
  "updatedAtUtc": "2026-10-13T06:00:00Z"
}
```

The PUT body contains the same five preference fields and omits `updatedAtUtc`. An omitted or null scalar means unspecified; omitted or null collections mean empty. PUT replaces the complete stored state atomically.

The options response returns `interests`, `preferredTransportModes`, `travelPaces`, `foodPreferences`, and `riskTolerances`. It has no budget range.

## Validation and persistence

- Maximum 10 distinct interests; commercial-service categories are rejected.
- `NoRestriction` is exclusive among food preferences.
- Unknown keys return the stable `TRAVEL_PREFERENCES.INVALID_OPTION` error code.
- Interest and food JSON each fit the existing 1,000-character storage limit.
- `dbo.TravelerProfiles` persists only `interest_tags_json`, `preferred_transport_mode`, `travel_pace`, `risk_tolerance`, `food_preferences_json`, and `updated_at` for this use case.

Migration `20261013_remove_traveler_profile_default_budget.sql` drops the obsolete `default_budget` column idempotently. It does not modify scheduling-request budgets.

## Security and compatibility

Controller and handler both enforce active-Traveler access. Responses are `Cache-Control: no-store`; errors expose stable codes, not message-catalog text or internal details. UC-10 may consume `preferredTransportMode` only as a client-side convenience; it never reads or writes profile budget data.
