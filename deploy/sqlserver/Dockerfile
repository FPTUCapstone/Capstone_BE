# Railway build source for the standalone `sqlserver` service (not part of the API image).
#
# The upstream mssql/server image runs as a non-root `mssql` user by default. Railway creates
# Volumes owned by root, so sqlservr fails at startup with "Access Denied" trying to initialize
# /var/opt/mssql/.system on a freshly attached Volume. Running as root avoids that permission
# mismatch — acceptable here since this is a single-tenant capstone database, not a
# multi-tenant/hardened production host.
FROM mcr.microsoft.com/mssql/server:2019-latest
USER root
