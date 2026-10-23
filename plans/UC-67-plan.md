# UC 67 Backend Plan

1. Apply the UC-67 migration through `database/apply-schema.sh` and verify the three reporting tables plus the unique indexes. Register the daily Vietnam-local period-close worker so later completed periods become eligible without a new migration.
2. Map `ServiceBooking`, closed periods, report snapshots, and export operations in EF Core; do not remap existing Booking or Refund entities.
3. Build Administrator-only period discovery and report generation. Validate the report/filter matrix before querying data, then create or retrieve the immutable criteria snapshot.
4. Calculate financial values from the UC-44 payment-rate snapshot and processed refund events. Keep ServiceBooking commission separate because it has no operator/destination ownership.
5. Render CSV/XLSX from the persisted report response and persist the caller-supplied export operation id.
6. Add API and SQL integration coverage for an open period, a repeated criterion, refund reversal, service-only scope, and duplicate export operation id. Run formatting, build, and the repository test suite in CI.
