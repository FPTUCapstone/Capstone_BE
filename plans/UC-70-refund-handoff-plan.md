# UC-70 refund handoff and weather notices — plan

Spec: `specs/UC-70-spec.md` (sections "Refund handoff" and "Weather warnings and escalation").
Branch: `feature/phuctv-uc70-refund-handoff`. No schema change, no migration.

| # | Task | Files | Verification |
| --- | --- | --- | --- |
| 1 | Cancel before departure: 100% `Refund` Pending per verified paid booking, with its `BookingCancellationOperation` (derived key) | `Emergency/Common/EmergencyRefundHandoff.cs`, `EmergencyTourCommandHandler.cs` | `EmergencyTourCommandHandlerTests` (handoff, reason text, replay) |
| 2 | Paid bookings that cannot be refunded automatically (emergency stop, unverified payment, earlier refund) → one in-app notice per active Administrator; audit `RefundHandoff` | `TripAlerts/Common/AdministratorInAppNotices.cs`, `EmergencyRefundHandoff.cs`, `Notification.cs` | `EmergencyTourCommandHandlerTests` (stop, earlier refund, audit, no notice when nothing to review) |
| 3 | Weather review created → in-app `WeatherWarning` notice to Travelers with open bookings | `Weather/Processing/WeatherAlertInboxProcessor.cs` | `WeatherAlertInboxProcessorTests` |
| 4 | Review escalated → in-app notice to active Administrators | `Infrastructure/Services/WeatherSafetyReviewEscalationBackgroundService.cs` | `WeatherSafetyReviewSqlServerTests.MissedSla_AlsoTellsEveryActiveAdministrator_InAppOnce` |
| 5 | `refundsSubmitted` on the Operator response (recomputed on replay); `refundHandling` on Traveler `emergency-status` | `EmergencyTourActionResponse.cs`, `GetTourBookingEmergencyStatusQuery.cs` | `EmergencyTourCommandHandlerTests`, `TravelerEmergencyStatusTests` |
| 6 | Real SQL Server: FK, CHECK, unique indexes, two saves in one serializable transaction, replay from a fresh context | — | `EmergencyRefundHandoffSqlServerTests` |
| 7 | `tourId` on Traveler `emergency-status` (rebooking button) | `GetTourBookingEmergencyStatusQuery.cs` | `TravelerEmergencyStatusTests` |
| 8 | Admin Active Trips `refundReviewRequired` | `Admin/ActiveTrips/GetList/*` | `GetActiveTripsQueryHandlerTests`, `BookedTourEmergencyFlowSqlServerTests` |
| 9 | SRS message wording MSG123/126/133; documented differences from SRS UC-66 | `Emergency/Common/EmergencyTourMessages.cs`, handlers, validators, spec | `EmergencyValidationMessageTests`, `EmergencyTourCommandHandlerTests` |

Definition of done: full `dotnet test TripMate.slnx` green with `TRIPMATE_SQLSERVER_TEST_CONNECTION` set (no
skipped SQL tests); `dotnet format --verify-no-changes` clean on the changed `.cs` files; no commit, no push.

Companion branches (same name): Mobile (refund copy, refund-progress and rebooking buttons, Operator
notice/result) and Web (Operator notice/result, Admin "refund review needed" badge, traveler stopped-tour
copy). Still outside: an Admin Web notification inbox.
