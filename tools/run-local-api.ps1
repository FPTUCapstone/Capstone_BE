$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$values = @{}
Get-Content '.env' | ForEach-Object {
    if ($_ -match '^\s*([^#=]+)=(.*)$') { $values[$matches[1].Trim()] = $matches[2] }
}
if ([string]::IsNullOrWhiteSpace($values['SA_PASSWORD']) -or [string]::IsNullOrWhiteSpace($values['JWT_SIGNING_KEY'])) {
    throw 'Required local configuration is missing.'
}
$dbPort = if ([string]::IsNullOrWhiteSpace($values['DB_HOST_PORT'])) { '14330' } else { $values['DB_HOST_PORT'] }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Default = "Server=localhost,$dbPort;Database=TripMateDb;User Id=sa;Password=$($values['SA_PASSWORD']);TrustServerCertificate=True;"
$env:ConnectionStrings__Redis = 'localhost:6379'
$env:Jwt__SigningKey = $values['JWT_SIGNING_KEY']
$env:AiRanking__Enabled = 'false'
$env:AiExplanation__Enabled = 'false'
$env:Cloudinary__RevenueExportsFolderRoot = 'tripmate/revenue-exports'
$apiRoot = Join-Path $repoRoot 'src\TripMate.Api'
& dotnet (Join-Path $apiRoot 'bin\Debug\net10.0\TripMate.Api.dll') --urls http://localhost:5000 --contentRoot $apiRoot
