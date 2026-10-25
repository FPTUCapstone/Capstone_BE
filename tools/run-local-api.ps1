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
$env:Payment__Provider = if ([string]::IsNullOrWhiteSpace($values['Payment__Provider'])) { 'Mock' } else { $values['Payment__Provider'] }
$apiPort = if ([string]::IsNullOrWhiteSpace($values['API_HOST_PORT'])) { '5021' } else { $values['API_HOST_PORT'] }
$apiRoot = Join-Path $repoRoot 'src\TripMate.Api'
& dotnet run --project $apiRoot --urls "http://localhost:$apiPort"
