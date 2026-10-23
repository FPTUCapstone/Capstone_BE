# UC 67 SRS Reconciliation

This change updates the UC-67 interpretation used by the implementation. Platform Revenue means recognized TripMate commission, not booking GMV. The supported export formats are CSV and XLSX; PDF is deferred. Reports use closed Vietnam-local calendar periods and immutable per-criteria snapshots. Booking Type covers All, Tour Package, and Commercial Service where the data model supports each dimension.

SRS message identifiers are retained only when their catalogue text matches the UC-67 event. Existing MSG29, MSG117, MSG136 through MSG138, and MSG149 are not reused. Add a new approved catalogue record when a user-facing reporting message has no semantic match.
