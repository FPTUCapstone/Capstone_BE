param([Parameter(Mandatory)][string]$RuntimeDirectory)
$ErrorActionPreference='Stop'
$secure=Get-Content "$RuntimeDirectory/runtime.dpapi"|ConvertTo-SecureString
$runtime=[Net.NetworkCredential]::new('', $secure).Password|ConvertFrom-Json
$container='tm70-semantic-e2e-dev-sql-1'
$details=docker inspect $container|ConvertFrom-Json|Select-Object -First 1
if($details.Id-cne$runtime.ContainerId -or $runtime.Database -notmatch '^TM70_SemanticDev_[a-f0-9]{32}$') {throw 'Owned fixture identity mismatch.'}
$env:TM70_DEV_CONNECTION=$runtime.Connection
$env:TM70_DEV_COLLECTION=$runtime.Collection
$env:TM70_DEV_EMBEDDING_URL='http://127.0.0.1:8017'
$env:TM70_DEV_QDRANT_URL='http://127.0.0.1:6337'
$env:TM70_DEV_INDEX_MANIFEST="$RuntimeDirectory/index-manifest.json"
function Index {
    $output=dotnet run --project "$PSScriptRoot/../TripMate.SearchDev" -c Release --no-build
    if($LASTEXITCODE-ne0){throw 'Dev index failed.'}
    return $output|ConvertFrom-Json
}
function Sql([string]$command) {
    ("SET QUOTED_IDENTIFIER ON;`nGO`n"+$command)|docker exec -i $container sh -c 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -C -U sa -b -W -h -1 -d "$1"' tm70-fixture $runtime.Database | Out-Null
    if($LASTEXITCODE-ne0){throw 'Owned fixture SQL failed.'}
}
$evidence=@()
function Search([string]$scenario,[int]$expected,[string]$code='',[int]$count=-1) {
    $r=Invoke-WebRequest 'http://127.0.0.1:5077/api/dev/tours/search?keyword=travel&mode=semantic' -Headers @{'X-TM70-Dev-Key'=$runtime.Key} -SkipHttpErrorCheck -TimeoutSec 30
    $content=if($r.Content-is[byte[]]){[Text.Encoding]::UTF8.GetString($r.Content)}else{$r.Content}
    $body=$content|ConvertFrom-Json
    $pass=[int]$r.StatusCode-eq$expected -and (!$code-or$body.errorCode-eq$code) -and ($count-lt0-or$body.totalCount-eq$count)
    $script:evidence+=@{scenario=$scenario;status=$(if($pass){'PASS'}else{'FAIL'});http=[int]$r.StatusCode;errorCode=$body.errorCode;traceId=$body.traceId;totalCount=$body.totalCount;ids=@($body.items|ForEach-Object tourId)}
    if(!$pass){throw "Failed $scenario"}
}
try {
    $first=Index; $second=Index
    if($first.indexedCount-ne4-or$second.indexedCount-ne4){throw 'Unexpected fixture count.'}
    $evidence+=@{scenario='Idempotent indexing';status='PASS';indexedCount=$second.indexedCount}
    Sql "UPDATE commerce.Tours SET title=N'Updated Hội An dev tour' WHERE tour_id=1;"
    Search 'Stale vector fails coverage' 503 'DevSearch.IndexIncomplete'
    $updated=Index
    Search 'Update reindex restores exact coverage' 200 '' 4
    # Remove only the exact owned point to test a missing vector. No collection deletion.
    Invoke-RestMethod "http://127.0.0.1:6337/collections/$($runtime.Collection)/points/delete?wait=true" -Method Post -ContentType 'application/json' -Body '{"points":[1]}' | Out-Null
    Search 'Missing vector fails coverage' 503 'DevSearch.IndexIncomplete'
    $repaired=Index
    Sql "UPDATE commerce.Tours SET status='Inactive' WHERE tour_id=1;"
    Search 'Unpublished Tour excluded while stale point remains' 200 '' 3
    $removed=Index
    if($removed.indexedCount-ne3-or$removed.removedCount-ne1){throw 'Reconciliation removal mismatch.'}
    Search 'Unpublication reconciliation' 200 '' 3
    Sql "UPDATE commerce.Tours SET title=N'Khám phá phố cổ Hội An',status='Approved' WHERE tour_id=1;"
    Search 'Republish missing point fails coverage' 503 'DevSearch.IndexIncomplete'
    $restored=Index
    Search 'Republish reindex restores coverage' 200 '' 4
} finally {
    # Restore only this task's explicit fixture row; never DROP/cleanup by prefix.
    Sql "UPDATE commerce.Tours SET title=N'Khám phá phố cổ Hội An',status='Approved' WHERE tour_id=1;"
    $restore=Index
    $evidence|ConvertTo-Json -Depth 6|Set-Content "$RuntimeDirectory/../index-lifecycle.json"
    Remove-Item Env:TM70_DEV_CONNECTION -ErrorAction SilentlyContinue
}
$evidence|ForEach-Object {"$($_.status) $($_.scenario) HTTP$($_.http)"}
