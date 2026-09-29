#!/usr/bin/env bash
# Applies tripmate_schema_v7.sql to the TripMateDb database.
# Safe to run repeatedly: a fresh database receives the full v7 schema and an
# existing v7 database receives every idempotent script in migrations/.
# Databases older than v7 still require the documented reset/version upgrade.
set -euo pipefail

SQLCMD="/opt/mssql-tools18/bin/sqlcmd"
DB_SERVER="${DB_SERVER:-sqlserver,1433}"
DB_NAME="${DB_NAME:-TripMateDb}"
: "${SA_PASSWORD:?SA_PASSWORD must be set}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCHEMA_FILE="$SCRIPT_DIR/tripmate_schema_v7.sql"

apply_incremental_migrations() {
  local migration
  shopt -s nullglob
  for migration in "$SCRIPT_DIR"/migrations/*.sql; do
    echo "Applying $(basename "$migration") to [$DB_NAME]..."
    "$SQLCMD" -b -V 11 -C -I -S "$DB_SERVER" -U sa -P "$SA_PASSWORD" -d "$DB_NAME" -i "$migration"
  done
}

echo "Ensuring database [$DB_NAME] exists on $DB_SERVER..."
"$SQLCMD" -b -V 11 -C -S "$DB_SERVER" -U sa -P "$SA_PASSWORD" -d master \
  -Q "IF DB_ID(N'$DB_NAME') IS NULL CREATE DATABASE [$DB_NAME];"

# 'empty'   -> nothing applied yet, safe to run the full script.
# 'current' -> v7 already present (MSG130 only exists from v7 onward), run migrations.
# 'stale'   -> an older version (e.g. v6) is applied; MSG130 is missing.
STATE=$("$SQLCMD" -b -V 11 -C -S "$DB_SERVER" -U sa -P "$SA_PASSWORD" -d "$DB_NAME" -h -1 -W \
  -Q "SET NOCOUNT ON;
      IF SCHEMA_ID(N'catalog') IS NULL SELECT 'empty';
      ELSE IF EXISTS (SELECT 1 FROM dbo.Messages WHERE message_code = 'MSG130') SELECT 'current';
      ELSE SELECT 'stale';" | tr -d '[:space:]')

case "$STATE" in
  current)
    echo "TripMate schema (v7) already present in [$DB_NAME] — skipping."
    apply_incremental_migrations
    exit 0
    ;;
  stale)
    echo "ERROR: [$DB_NAME] has an older schema version applied (missing MSG130 seed data)." >&2
    echo "No verified upgrade path from this version is included. Preserve the database and ask the data owner for a backup and migration plan." >&2
    echo "Do not run the full schema or remove the SQL Server volume on a database with data." >&2
    exit 1
    ;;
esac

echo "Applying $(basename "$SCHEMA_FILE") to [$DB_NAME]..."
# -I: turn on QUOTED_IDENTIFIER for the session — required for the filtered/unique
# indexes in this script (sqlcmd defaults it OFF; SSMS would have set it ON for you).
"$SQLCMD" -b -V 11 -C -I -S "$DB_SERVER" -U sa -P "$SA_PASSWORD" -d "$DB_NAME" -i "$SCHEMA_FILE"
apply_incremental_migrations
echo "Schema applied successfully."
