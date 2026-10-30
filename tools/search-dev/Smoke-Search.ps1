param([Parameter(Mandatory)][string]$RuntimeDirectory, [string]$BaseUrl='http://127.0.0.1:5077')
$ErrorActionPreference='Stop'
if ($BaseUrl -notmatch '^http://127\.0\.0\.1:[0-9]+$') { throw 'Only local dev API is allowed.' }
$key=(Get-Content "$RuntimeDirectory/dev-access.key" -Raw).Trim()
$cases=@(
    @{name='Vietnamese semantic';q='keyword=ngh%E1%BB%89%20d%C6%B0%E1%BB%A1ng%20bi%E1%BB%83n&mode=semantic&sort=relevance';ids=@('1','2','3','6');count=4},
    @{name='English semantic';q='keyword=quiet%20beach%20holiday&mode=semantic&sort=relevance';ids=@('1','2','3','6');count=4},
    @{name='Lexical accent insensitive';q='keyword=hoi%20an&mode=lexical&sort=relevance';ids=@('1');count=1},
    @{name='Hybrid';q='keyword=H%E1%BB%99i%20An&mode=hybrid&sort=relevance';ids=@('1','2','3','6');count=4},
    @{name='Destination';q='keyword=travel&mode=semantic&destination=da%20nang';ids=@('2');count=1},
    @{name='Price inclusive';q='minPrice=900000&maxPrice=1200000&sort=priceAsc';ids=@('1','3');count=2},
    @{name='Duration category rating';q='durationDays=2&category=dev-culture&minimumRating=4.5';ids=@('1');count=1},
    @{name='Raw rating';q='minimumRating=4.51&sort=rating';ids=@('3');count=1},
    @{name='Future date';q='startDate=2026-12-10&endDate=2026-12-10';ids=@('1');count=1},
    @{name='Past completed date';q='startDate=2026-01-01&endDate=2026-01-01';ids=@('6');count=1},
    @{name='Visibility';q='sort=priceAsc';ids=@('1','2','3','6');count=4},
    @{name='Pagination';q='mode=semantic&keyword=travel&sort=priceAsc&page=2&pageSize=2';ids=@('1','2');count=4},
    @{name='Huge empty page';q='page=2147483647&pageSize=100';ids=@();count=4},
    @{name='No lexical result';q='keyword=zzzz_unrelated_abcdef&mode=lexical';ids=@();count=0},
    @{name='No filtered result';q='keyword=travel&mode=semantic&maxPrice=0';ids=@();count=0}
)
$evidence=@()
function ContentJson($response) {
    $content=if($response.Content-is[byte[]]){[Text.Encoding]::UTF8.GetString($response.Content)}else{$response.Content}
    return $content|ConvertFrom-Json
}
foreach($case in $cases) {
    $r=Invoke-WebRequest "$BaseUrl/api/dev/tours/search?$($case.q)" -Headers @{'X-TM70-Dev-Key'=$key} -SkipHttpErrorCheck -TimeoutSec 45
    $body=ContentJson $r
    $ids=@($body.items|ForEach-Object tourId)
    $same=@(Compare-Object @($ids|Sort-Object) @($case.ids|Sort-Object)).Count -eq 0
    $pass=$r.StatusCode -eq 200 -and $body.totalCount -eq $case.count -and $same -and $r.Headers.'Cache-Control' -contains 'no-store'
    $evidence+=@{scenario=$case.name;status=$(if($pass){'PASS'}else{'FAIL'});http=[int]$r.StatusCode;query=$case.q;ids=$ids;totalCount=$body.totalCount;totalPages=$body.totalPages;trace=$r.Headers.'X-TM70-Trace-Id';errorCode=$body.errorCode}
}
$longQuery=[Uri]::EscapeDataString(('a '*249)+'b')
$longResponse=Invoke-WebRequest "$BaseUrl/api/dev/tours/search?keyword=$longQuery&mode=lexical" -Headers @{'X-TM70-Dev-Key'=$key} -SkipHttpErrorCheck -TimeoutSec 30
$longBody=ContentJson $longResponse
$evidence+=@{scenario='499-character 250-token SQL regression';status=$(if($longResponse.StatusCode-eq200-and$longBody.totalCount-ge0){'PASS'}else{'FAIL'});http=[int]$longResponse.StatusCode;totalCount=$longBody.totalCount;trace=$longResponse.Headers.'X-TM70-Trace-Id'}
foreach($security in @(
    @{name='Missing key';headers=@{};q=''},
    @{name='Wrong key';headers=@{'X-TM70-Dev-Key'='wrong'};q=''},
    @{name='Invalid mode';headers=@{'X-TM70-Dev-Key'=$key};q='mode=unexpected'},
    @{name='Invalid date range';headers=@{'X-TM70-Dev-Key'=$key};q='startDate=2026-12-20&endDate=2026-12-01'},
    @{name='Inactive category';headers=@{'X-TM70-Dev-Key'=$key};q='category=dev-inactive'}
)) {
    $r=Invoke-WebRequest "$BaseUrl/api/dev/tours/search?$($security.q)" -Headers $security.headers -SkipHttpErrorCheck -TimeoutSec 20
    $expected=if($security.name -match 'key'){401}else{400}
    $evidence+=@{scenario=$security.name;status=$(if($r.StatusCode-eq$expected){'PASS'}else{'FAIL'});http=[int]$r.StatusCode;query=$security.q;trace=$r.Headers.'X-TM70-Trace-Id'}
}
$evidence|ConvertTo-Json -Depth 6|Set-Content "$RuntimeDirectory/../backend-http-smoke.json"
$evidence | ForEach-Object { "{0} HTTP{1} {2}" -f $_.status,$_.http,$_.scenario }
if(@($evidence|Where-Object status -eq FAIL).Count){throw 'Smoke scenarios failed; inspect sanitized evidence.'}
