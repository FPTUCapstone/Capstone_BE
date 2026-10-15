#!/usr/bin/env bash
# Applies tripmate_schema_v7.sql and database/migrations/*.sql to an existing Azure SQL database.
#
# Same decision logic as apply-schema.sh, but it signs in with a SQL login instead of `sa`
# and does not try to CREATE DATABASE (Azure SQL databases are created in the portal):
#   empty   -> apply the full v7 schema, then every migration
#   current -> v7 already present, apply the migrations that are not recorded yet
#   stale   -> an older schema is present; stop without touching anything
#
# Migration ledger (dbo.SchemaMigrations): file_name, checksum (SHA-256), status, applied_at.
#   status 'Applying' -> written BEFORE a file runs; 'Applied' -> written after it succeeded;
#   status 'Stamped'  -> recorded as already applied WITHOUT running it (baseline).
# Historical migrations are immutable, so the script refuses (exit 1) when:
#   * a recorded migration file has changed (checksum mismatch),
#   * a migration is still 'Applying' (an earlier run was interrupted or failed), or
#   * the ledger does not exist yet on a database that already has the schema, unless a baseline
#     is given. Nothing is executed until the whole plan is checked, so a refusal changes nothing.
#
# Optional environment (all default to safe values):
#   DRY_RUN=true                 print the plan and exit; makes no change, not even the ledger table
#   STAMP_ONLY=a.sql,b.sql       baseline: record these files as already applied without running them
#   REAPPLY_CHANGED=a.sql        explicitly allow re-running a recorded file whose content changed
#   ALLOW_REAPPLY_ALL=true       allow running every migration on an existing database with no ledger
#   TRUST_SERVER_CERTIFICATE=true  skip TLS certificate validation (local self-signed SQL Server only;
#                                  never for Azure SQL, which presents a publicly trusted certificate)
#   DB_NAME                      default TripMateDb
# Required: AZ_SERVER, AZ_USER, AZ_PASS. Run it inside an image that ships sqlcmd, for example
# mcr.microsoft.com/mssql/server:2022-latest.
#
# Reconciling an interrupted migration: check in the database whether the file's changes exist, then
#   UPDATE dbo.SchemaMigrations SET status = 'Applied' WHERE file_name = N'<file>';   -- it did apply
#   DELETE FROM dbo.SchemaMigrations WHERE file_name = N'<file>';                      -- it did not
set -euo pipefail

: "${AZ_SERVER:?AZ_SERVER must be set}"
: "${AZ_USER:?AZ_USER must be set}"
: "${AZ_PASS:?AZ_PASS must be set}"
DB_NAME="${DB_NAME:-TripMateDb}"
DRY_RUN="${DRY_RUN:-false}"
STAMP_ONLY="${STAMP_ONLY:-}"
REAPPLY_CHANGED="${REAPPLY_CHANGED:-}"
ALLOW_REAPPLY_ALL="${ALLOW_REAPPLY_ALL:-false}"
SQLCMD="/opt/mssql-tools18/bin/sqlcmd"

TRUST=()
if [ "${TRUST_SERVER_CERTIFICATE:-false}" = "true" ]; then
  TRUST=(-C)
  echo "WARNING: TLS certificate validation is disabled (TRUST_SERVER_CERTIFICATE=true)." >&2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCHEMA_FILE="$SCRIPT_DIR/tripmate_schema_v7.sql"
NAME_RE='^[A-Za-z0-9_.-]+\.sql$'

# -I enables QUOTED_IDENTIFIER (required by the filtered/unique indexes); -l is the login timeout.
sql_file() { "$SQLCMD" -b ${TRUST[@]+"${TRUST[@]}"} -I -l 120 -S "$AZ_SERVER" -U "$AZ_USER" -P "$AZ_PASS" -d "$DB_NAME" -i "$1"; }
sql_query() { "$SQLCMD" -b ${TRUST[@]+"${TRUST[@]}"} -l 120 -S "$AZ_SERVER" -U "$AZ_USER" -P "$AZ_PASS" -d "$DB_NAME" -h -1 -W -Q "$1"; }

declare -A STAMP=() REAPPLY=()
parse_list() { # parse_list <list> <assoc-array-name>
  local item
  local -n target="$2"
  for item in ${1//,/ }; do
    if [[ ! "$item" =~ $NAME_RE ]] || [ ! -f "$SCRIPT_DIR/migrations/$item" ]; then
      echo "ERROR: '$item' is not a migration file in database/migrations." >&2
      exit 1
    fi
    target["$item"]=1
  done
}
parse_list "$STAMP_ONLY" STAMP
parse_list "$REAPPLY_CHANGED" REAPPLY

# A serverless database may be paused; the first connection wakes it (can take about a minute).
echo "Connecting to $DB_NAME on $AZ_SERVER (a paused serverless database needs time to wake up)..."
for attempt in 1 2 3 4 5; do
  if sql_query "SELECT 1" >/dev/null 2>&1; then
    echo "Connected."
    break
  fi
  if [ "$attempt" -eq 5 ]; then
    echo "ERROR: cannot connect to $AZ_SERVER (check credentials, the Azure SQL firewall and TLS)." >&2
    exit 1
  fi
  echo "  attempt $attempt failed, retrying in 15s..."
  sleep 15
done

STATE=$(sql_query "SET NOCOUNT ON;
  IF SCHEMA_ID(N'catalog') IS NULL SELECT 'empty';
  ELSE IF EXISTS (SELECT 1 FROM dbo.Messages WHERE message_code = 'MSG130') SELECT 'current';
  ELSE SELECT 'stale';" | tr -d '[:space:]')

case "$STATE" in
  stale)
    echo "ERROR: [$DB_NAME] has an older schema version (missing MSG130 seed data). Nothing was changed." >&2
    echo "Preserve the database and ask the data owner for a backup and migration plan." >&2
    exit 1
    ;;
  empty|current) ;;
  *)
    echo "ERROR: unexpected schema state '$STATE'. Nothing was changed." >&2
    exit 1
    ;;
esac

LEDGER_EXISTS=$(sql_query "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'dbo.SchemaMigrations', N'U') IS NULL THEN 0 ELSE 1 END;" | tr -d '[:space:]')

if [ "$STATE" = "current" ] && [ "$LEDGER_EXISTS" = "0" ] && [ -z "$STAMP_ONLY" ] && [ "$ALLOW_REAPPLY_ALL" != "true" ]; then
  echo "ERROR: [$DB_NAME] already has the schema but no migration ledger. Nothing was changed." >&2
  echo "Baseline it first: run with STAMP_ONLY=<files already applied> (they are recorded, not executed)," >&2
  echo "or set ALLOW_REAPPLY_ALL=true to run every migration (only if you have verified that is safe)." >&2
  exit 1
fi

# ---- Plan: decide the action for every file before changing anything ------------------------
shopt -s nullglob
declare -a FILES=() ACTIONS=() SUMS=()
blocked=0
for migration in "$SCRIPT_DIR"/migrations/*.sql; do
  name="$(basename "$migration")"
  if [[ ! "$name" =~ $NAME_RE ]]; then
    echo "ERROR: unsafe migration file name: $name" >&2
    exit 1
  fi
  sum="$(sha256sum "$migration" | cut -d' ' -f1)"
  rec=""
  if [ "$LEDGER_EXISTS" = "1" ]; then
    rec="$(sql_query "SET NOCOUNT ON; SELECT status + '|' + checksum FROM dbo.SchemaMigrations WHERE file_name = N'$name';" | tr -d '[:space:]')"
  fi
  rec_status="${rec%%|*}"
  rec_sum="${rec##*|}"

  if [ -z "$rec" ]; then
    if [ -n "${STAMP[$name]:-}" ]; then action="stamp"; else action="apply"; fi
  elif [ "$rec_status" = "Applying" ]; then
    action="BLOCKED-interrupted"
  elif [ "$rec_sum" = "$sum" ]; then
    action="skip"
  elif [ -n "${REAPPLY[$name]:-}" ]; then
    action="reapply"
  else
    action="BLOCKED-changed"
  fi
  FILES+=("$name"); ACTIONS+=("$action"); SUMS+=("$sum")
  [[ "$action" == BLOCKED-* ]] && blocked=1
done

echo "Plan for [$DB_NAME] (state: $STATE, ledger: $([ "$LEDGER_EXISTS" = "1" ] && echo present || echo missing)):"
for i in "${!FILES[@]}"; do printf '  %-10s %s\n' "${ACTIONS[$i]}" "${FILES[$i]}"; done

if [ "$blocked" -eq 1 ]; then
  echo "ERROR: blocked migrations found. Nothing was changed." >&2
  echo "  BLOCKED-changed     a recorded migration was edited. Migrations are immutable: add a new forward" >&2
  echo "                      migration, or re-run with REAPPLY_CHANGED=<file> after reviewing the change." >&2
  echo "  BLOCKED-interrupted an earlier run did not finish this file. Reconcile it (see the script header)." >&2
  exit 1
fi

if [ "$DRY_RUN" = "true" ]; then
  echo "DRY_RUN=true: no changes were made."
  exit 0
fi

# ---- Execute -------------------------------------------------------------------------------
if [ "$STATE" = "empty" ]; then
  echo "Applying $(basename "$SCHEMA_FILE")..."
  sql_file "$SCHEMA_FILE"
fi

sql_query "SET NOCOUNT ON;
  IF OBJECT_ID(N'dbo.SchemaMigrations', N'U') IS NULL
    CREATE TABLE dbo.SchemaMigrations (
      file_name  NVARCHAR(260) NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY,
      checksum   CHAR(64)      NOT NULL,
      status     VARCHAR(10)   NOT NULL CONSTRAINT CK_SchemaMigrations_Status CHECK (status IN ('Applying','Applied','Stamped')),
      applied_at DATETIME2     NOT NULL CONSTRAINT DF_SchemaMigrations_applied_at DEFAULT SYSUTCDATETIME()
    );" >/dev/null

for i in "${!FILES[@]}"; do
  name="${FILES[$i]}"; sum="${SUMS[$i]}"
  case "${ACTIONS[$i]}" in
    skip) ;;
    stamp)
      echo "Stamping $name (recorded as applied, not executed)"
      sql_query "SET NOCOUNT ON; INSERT INTO dbo.SchemaMigrations (file_name, checksum, status) VALUES (N'$name', '$sum', 'Stamped');" >/dev/null
      ;;
    apply|reapply)
      echo "Applying $name..."
      # Record the intent first. If the run dies before the file is confirmed, the next run sees
      # 'Applying' and refuses to repeat it blindly.
      sql_query "SET NOCOUNT ON;
        MERGE dbo.SchemaMigrations AS t
        USING (SELECT N'$name' AS file_name, '$sum' AS checksum) AS s ON t.file_name = s.file_name
        WHEN MATCHED THEN UPDATE SET checksum = s.checksum, status = 'Applying', applied_at = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT (file_name, checksum, status) VALUES (s.file_name, s.checksum, 'Applying');" >/dev/null
      if ! sql_file "$SCRIPT_DIR/migrations/$name"; then
        echo "ERROR: $name failed. It stays marked 'Applying' so it is not repeated blindly." >&2
        echo "Check the database, then reconcile it (see the script header)." >&2
        exit 1
      fi
      sql_query "SET NOCOUNT ON; UPDATE dbo.SchemaMigrations SET status = 'Applied', applied_at = SYSUTCDATETIME() WHERE file_name = N'$name' AND checksum = '$sum';" >/dev/null
      ;;
  esac
done

echo "DONE"
