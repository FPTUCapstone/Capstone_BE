param([Parameter(Mandatory)][string]$RuntimeDirectory)
$ErrorActionPreference='Stop'
$secure=Get-Content "$RuntimeDirectory/runtime.dpapi"|ConvertTo-SecureString
$runtime=[Net.NetworkCredential]::new('', $secure).Password|ConvertFrom-Json
$details=docker inspect tm70-semantic-e2e-dev-sql-1|ConvertFrom-Json|Select-Object -First 1
if($details.Id-cne$runtime.ContainerId){throw 'SQL dev identity mismatch.'}
$env:ConnectionStrings__Default=$runtime.Connection
$env:DevTourSearch__Key=$runtime.Key
$env:DevTourSearch__Enabled='true'
$env:DevTourSearch__Collection=$runtime.Collection
$env:DevTourSearch__EmbeddingUrl='http://127.0.0.1:8017'
$env:DevTourSearch__QdrantUrl='http://127.0.0.1:6337'
$env:DevTourSearch__RrfK='60'
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:ASPNETCORE_URLS='http://127.0.0.1:5077'
$env:Jwt__SigningKey=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
$env:AiRanking__Enabled='false'
$env:AiExplanation__Enabled='false'
$env:SchedulingRateLimit__Provider='SingleInstance'
# Existing non-search startup validation requires media settings. These placeholders
# carry no external authority; SQL login has no write permissions or media work rows.
$env:Cloudinary__CloudName='tm70-local-placeholder'
$env:Cloudinary__ApiKey='000000000000000'
$env:Cloudinary__ApiSecret='local-placeholder-no-provider-authority'
$env:Cloudinary__TourMediaFolderRoot='tm70-dev/tours'
$env:Cloudinary__OperatorDocumentsFolderRoot='tm70-dev/operators'
Push-Location (Resolve-Path "$PSScriptRoot/../..")
try { dotnet run --project src/TripMate.Api -c Release --no-build --no-launch-profile }
finally {
    Pop-Location
    Remove-Item Env:ConnectionStrings__Default,Env:DevTourSearch__Key,Env:Jwt__SigningKey -ErrorAction SilentlyContinue
}
