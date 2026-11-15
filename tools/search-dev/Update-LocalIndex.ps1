param([Parameter(Mandatory)][string]$RuntimeDirectory)
$ErrorActionPreference='Stop'
$secure=Get-Content "$RuntimeDirectory/runtime.dpapi"|ConvertTo-SecureString
$runtime=[Net.NetworkCredential]::new('', $secure).Password|ConvertFrom-Json
$details=docker inspect tm70-semantic-e2e-dev-sql-1|ConvertFrom-Json|Select-Object -First 1
if($details.Id-cne$runtime.ContainerId){throw 'SQL dev identity mismatch.'}
$env:TM70_DEV_CONNECTION=$runtime.Connection
$env:TM70_DEV_COLLECTION=$runtime.Collection
$env:TM70_DEV_EMBEDDING_URL='http://127.0.0.1:8017'
$env:TM70_DEV_QDRANT_URL='http://127.0.0.1:6337'
$env:TM70_DEV_INDEX_MANIFEST="$RuntimeDirectory/index-manifest.json"
try { dotnet run --project "$PSScriptRoot/../TripMate.SearchDev" -c Release --no-build }
finally { Remove-Item Env:TM70_DEV_CONNECTION -ErrorAction SilentlyContinue }
