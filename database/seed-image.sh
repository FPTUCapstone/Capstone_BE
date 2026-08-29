#!/usr/bin/env bash
# Runs ONLY during `docker build` of database/Dockerfile.seeded — starts SQL Server temporarily,
# applies tripmate_schema_v7.sql, then shuts it down so the resulting layer (with TripMateDb
# already created) gets committed into the image itself. Not used at container runtime.
set -euo pipefail

SQLCMD="/opt/mssql-tools18/bin/sqlcmd"

/opt/mssql/bin/sqlservr --accept-eula &
SQLPID=$!

echo "Waiting for SQL Server to start..."
for _ in $(seq 1 60); do
  if "$SQLCMD" -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1" >/dev/null 2>&1; then
    echo "SQL Server is up."
    break
  fi
  sleep 1
done

"$SQLCMD" -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d master \
  -Q "CREATE DATABASE TripMateDb;"

"$SQLCMD" -C -I -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d TripMateDb \
  -i /tmp/tripmate_schema_v7.sql

echo "Schema seeded. Shutting down SQL Server so the data gets committed into the image..."
kill -SIGTERM "$SQLPID"
wait "$SQLPID" || true
