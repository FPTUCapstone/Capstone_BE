param([Parameter(Mandatory)][string]$ExpectedContainerId, [Parameter(Mandatory)][string]$OutputDirectory,
    [string[]]$ExpectedExistingDatabases = @())
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$container = 'tm70-semantic-e2e-dev-sql-1'
$details = docker inspect $container | ConvertFrom-Json | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or $details.Id -cne $ExpectedContainerId -or
    $details.Config.Labels.'com.docker.compose.project' -cne 'tm70-semantic-e2e-dev' -or
    $details.Mounts.Count -ne 1 -or $details.Mounts[0].Name -cne 'tm70-semantic-e2e-dev_sql-data' -or
    $details.NetworkSettings.Ports.'1433/tcp'[0].HostIp -cne '127.0.0.1' -or
    $details.NetworkSettings.Ports.'1433/tcp'[0].HostPort -cne '14337') { throw 'Unverified disposable SQL container.' }
function Invoke-DevSql([string]$Database, [string]$Sql) {
    ("SET QUOTED_IDENTIFIER ON;`nGO`n" + $Sql) | docker exec -i $container sh -c 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -C -U sa -b -W -h -1 -d "$1"' tm70-observer $Database
    if ($LASTEXITCODE -ne 0) { throw 'Disposable SQL operation failed.' }
}
$identity = Invoke-DevSql 'master' "SET NOCOUNT ON; SELECT CONCAT(CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)), '|', COALESCE(CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), 'DEFAULT'), '|', CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(256))); SELECT name FROM sys.databases WHERE database_id>4;"
$existingNames = @($identity | Where-Object { $_.Trim() -and $_ -notmatch '\|' } | ForEach-Object { $_.Trim() } | Sort-Object)
if (@($identity | Where-Object { $_ -match '\|' }).Count -ne 1 -or
    ($existingNames -join "`n") -cne (@($ExpectedExistingDatabases | Sort-Object) -join "`n")) { throw 'Unexpected databases in disposable instance.' }
$id = [Guid]::NewGuid().ToString('N')
$database = "TM70_SemanticDev_$id"
$login = "tm70_read_$id"
$password = 'Dev!'+[Guid]::NewGuid().ToString('N')+'9a'
$devKey = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Credential output must be outside the repository.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (Test-Path "$output/runtime.dpapi") { throw 'Refusing to overwrite a previous runtime manifest.' }
Invoke-DevSql 'master' "CREATE DATABASE [$database] COLLATE Vietnamese_100_CI_AS;"
@{ContainerId=$details.Id;Database=$database;CreatedAtUtc=[DateTime]::UtcNow.ToString('o');State='created-not-initialized'} | ConvertTo-Json -Compress | Add-Content "$output/creation-history.jsonl"
Invoke-DevSql $database (Get-Content "$repoRoot/database/tripmate_schema_v7.sql" -Raw)
foreach ($migration in Get-ChildItem "$repoRoot/database/migrations/*.sql" | Sort-Object Name) {
    Invoke-DevSql $database (Get-Content $migration.FullName -Raw)
}
Invoke-DevSql $database (Get-Content "$PSScriptRoot/fixtures.sql" -Raw)
Invoke-DevSql 'master' "CREATE LOGIN [$login] WITH PASSWORD=N'$password', CHECK_POLICY=ON;"
Invoke-DevSql $database "CREATE USER [$login] FOR LOGIN [$login]; GRANT SELECT ON SCHEMA::dbo TO [$login]; GRANT SELECT ON SCHEMA::catalog TO [$login]; GRANT SELECT ON SCHEMA::commerce TO [$login]; GRANT SELECT ON SCHEMA::social TO [$login];"
$runtime = @{
    ContainerId=$details.Id; Database=$database; Collection="tm70_poc_dev_$id"; Key=$devKey
    Connection="Server=127.0.0.1,14337;Database=$database;User ID=$login;Password=$password;Encrypt=True;TrustServerCertificate=True;Application Name=TM70-Dev-ReadOnly"
}
$runtime | ConvertTo-Json -Compress | ConvertTo-SecureString -AsPlainText -Force | ConvertFrom-SecureString | Set-Content "$output/runtime.dpapi"
@{ContainerId=$details.Id;Database=$database;State='initialized';FixtureTours=6} | ConvertTo-Json | Set-Content "$output/creation.json"
Invoke-DevSql $database "SET NOCOUNT ON; SELECT DB_NAME() AS database_name, (SELECT COUNT(*) FROM commerce.Tours) AS fixture_tours; SELECT physical_name FROM sys.database_files;"
Write-Output "Ready: $database; SQL SELECT-only principal; credentials protected by current Windows user DPAPI."
