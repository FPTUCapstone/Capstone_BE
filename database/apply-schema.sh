#!/usr/bin/env bash
# Applies tripmate_schema_v7.sql to the TripMateDb database.
# Safe to run repeatedly on a database that already has v7: skips silently.
# Refuses (with an actionable message) to run against a database that still
# has an older schema version — there is no incremental upgrade path here,
# each version is a full recreate script, so an older DB must be reset first.
set -euo pipefail

SQLCMD="/opt/mssql-tools18/bin/sqlcmd"
DB_SERVER="${DB_SERVER:-sqlserver,1433}"
DB_NAME="${DB_NAME:-TripMateDb}"
: "${SA_PASSWORD:?SA_PASSWORD must be set}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCHEMA_FILE="$SCRIPT_DIR/tripmate_schema_v7.sql"

echo "Ensuring database [$DB_NAME] exists on $DB_SERVER..."
"$SQLCMD" -C -S "$DB_SERVER" -U sa -P "$SA_PASSWORD" -d master \
  -Q "IF DB_ID(N'$DB_NAME') IS NULL CREATE DATABASE [$DB_NAME];"

# 'empty'   -> nothing applied yet, safe to run the full script.
# 'current' -> v7 already present (MSG130 only exists from v7 onward), skip.
# 'stale'   -> an older version (e.g. v6) is applied; MSG130 is missing.
STATE=$("$SQLCMD" -C -S "$DB_SERVER" -U sa -P "$SA_PASSWORD" -d "$DB_NAME" -h -1 -W \
  -Q "SET NOCOUNT ON;
      IF SCHEMA_ID(N'catalog') IS NULL SELECT 'empty';
      ELSE IF EXISTS (SELECT 1 FROM dbo.Messages WHERE message_code = 'MSG130') SELECT 'current';
      ELSE SELECT 'stale';" | tr -d '[:space:]')

case "$STATE" in
  current)
    echo "TripMate schema (v7) already present in [$DB_NAME] — skipping."
    exit 0
    ;;
  stale)
    echo "ERROR: [$DB_NAME] has an older schema version applied (missing MSG130 seed data)." >&2
    echo "This project has no incremental upgrade path — each version is a full recreate script." >&2
    echo "Reset the database first: docker compose down -v && docker compose up -d" >&2
    exit 1
    ;;
esac

echo "Applying $(basename "$SCHEMA_FILE") to [$DB_NAME]..."
# -I: turn on QUOTED_IDENTIFIER for the session — required for the filtered/unique
# indexes in this script (sqlcmd defaults it OFF; SSMS would have set it ON for you).
"$SQLCMD" -C -I -S "$DB_SERVER" -U sa -P "$SA_PASSWORD" -d "$DB_NAME" -i "$SCHEMA_FILE"
echo "Schema applied successfully."
