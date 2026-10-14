#!/usr/bin/env bash
# Applies tripmate_schema_v7.sql and database/migrations/*.sql to an existing Azure SQL database.
#
# Same decision logic as apply-schema.sh, but it signs in with a SQL login instead of `sa`
# and does not try to CREATE DATABASE (Azure SQL databases are created in the portal):
#   empty   -> apply the full v7 schema, then every migration
#   current -> v7 already present, apply only the (idempotent) migrations
#   stale   -> an older schema is present; stop without touching anything
#
# Migration ledger: dbo.SchemaMigrations records every applied migration file with its SHA-256.
# A file is applied only when it is missing from the ledger or its content changed since it was
# recorded (migrations are idempotent, so re-applying a corrected file is safe, and a warning is
# printed). The first run against a database without a ledger therefore applies every file once
# (as before) and records them; later runs apply only new or changed files.
#
# Required environment: AZ_SERVER, AZ_USER, AZ_PASS. Optional: DB_NAME (default TripMateDb).
# Run inside an image that ships sqlcmd, e.g. mcr.microsoft.com/mssql/server:2022-latest.
set -euo pipefail

: "${AZ_SERVER:?AZ_SERVER must be set}"
: "${AZ_USER:?AZ_USER must be set}"
: "${AZ_PASS:?AZ_PASS must be set}"
DB_NAME="${DB_NAME:-TripMateDb}"
SQLCMD="/opt/mssql-tools18/bin/sqlcmd"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCHEMA_FILE="$SCRIPT_DIR/tripmate_schema_v7.sql"

# -I enables QUOTED_IDENTIFIER (required by the filtered/unique indexes); -l is the login timeout.
sql_file() { "$SQLCMD" -b -C -I -l 120 -S "$AZ_SERVER" -U "$AZ_USER" -P "$AZ_PASS" -d "$DB_NAME" -i "$1"; }
sql_query() { "$SQLCMD" -b -C -l 120 -S "$AZ_SERVER" -U "$AZ_USER" -P "$AZ_PASS" -d "$DB_NAME" -h -1 -W -Q "$1"; }

# A serverless database may be paused; the first connection wakes it (can take about a minute).
echo "Connecting to $DB_NAME on $AZ_SERVER (a paused serverless database needs time to wake up)..."
for attempt in 1 2 3 4 5; do
  if sql_query "SELECT 1" >/dev/null 2>&1; then
    echo "Connected."
    break
  fi
  if [ "$attempt" -eq 5 ]; then
    echo "ERROR: cannot connect to $AZ_SERVER (check credentials and the Azure SQL firewall)." >&2
    exit 1
  fi
  echo "  attempt $attempt failed, retrying in 15s..."
  sleep 15
done

ensure_ledger() {
  sql_query "SET NOCOUNT ON;
    IF OBJECT_ID(N'dbo.SchemaMigrations', N'U') IS NULL
      CREATE TABLE dbo.SchemaMigrations (
        file_name  NVARCHAR(260) NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY,
        checksum   CHAR(64)      NOT NULL,
        applied_at DATETIME2     NOT NULL CONSTRAINT DF_SchemaMigrations_applied_at DEFAULT SYSUTCDATETIME()
      );" >/dev/null
}

apply_migrations() {
  local migration name sum recorded
  ensure_ledger
  shopt -s nullglob
  for migration in "$SCRIPT_DIR"/migrations/*.sql; do
    name="$(basename "$migration")"
    # The name is interpolated into SQL below; only plain file names are accepted.
    if [[ ! "$name" =~ ^[A-Za-z0-9_.-]+\.sql$ ]]; then
      echo "ERROR: unsafe migration file name: $name" >&2
      exit 1
    fi
    sum="$(sha256sum "$migration" | cut -d' ' -f1)"
    recorded="$(sql_query "SET NOCOUNT ON; SELECT checksum FROM dbo.SchemaMigrations WHERE file_name = N'$name';" | tr -d '[:space:]')"

    if [ "$recorded" = "$sum" ]; then
      echo "Skipping $name (already applied)"
      continue
    fi
    if [ -n "$recorded" ]; then
      echo "WARNING: $name changed since it was applied; re-applying (migrations must be idempotent)."
    fi

    echo "Applying $name..."
    sql_file "$migration"
    sql_query "SET NOCOUNT ON;
      MERGE dbo.SchemaMigrations AS t
      USING (SELECT N'$name' AS file_name, '$sum' AS checksum) AS s ON t.file_name = s.file_name
      WHEN MATCHED THEN UPDATE SET checksum = s.checksum, applied_at = SYSUTCDATETIME()
      WHEN NOT MATCHED THEN INSERT (file_name, checksum) VALUES (s.file_name, s.checksum);" >/dev/null
  done
}

STATE=$(sql_query "SET NOCOUNT ON;
  IF SCHEMA_ID(N'catalog') IS NULL SELECT 'empty';
  ELSE IF EXISTS (SELECT 1 FROM dbo.Messages WHERE message_code = 'MSG130') SELECT 'current';
  ELSE SELECT 'stale';" | tr -d '[:space:]')

case "$STATE" in
  current)
    echo "TripMate schema (v7) already present in [$DB_NAME] - applying migrations only."
    apply_migrations
    echo "DONE"
    ;;
  empty)
    echo "Applying $(basename "$SCHEMA_FILE")..."
    sql_file "$SCHEMA_FILE"
    apply_migrations
    echo "DONE"
    ;;
  stale)
    echo "ERROR: [$DB_NAME] has an older schema version (missing MSG130 seed data). Nothing was changed." >&2
    echo "Preserve the database and ask the data owner for a backup and migration plan." >&2
    exit 1
    ;;
  *)
    echo "ERROR: unexpected schema state '$STATE'. Nothing was changed." >&2
    exit 1
    ;;
esac
