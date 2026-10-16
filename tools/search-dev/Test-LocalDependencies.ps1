param([Parameter(Mandatory)][string]$RuntimeDirectory)
$ErrorActionPreference='Stop'
$key=(Get-Content "$RuntimeDirectory/dev-access.key" -Raw).Trim()
$inventory=Get-Content "$RuntimeDirectory/../stack-identity.json" -Raw|ConvertFrom-Json
$evidence=@()
foreach($case in @(
    @{name='Embedding timeout';container='tm70-semantic-e2e-dev-embedding-1';operation='pause'},
    @{name='Qdrant timeout';container='tm70-semantic-e2e-dev-qdrant-1';operation='pause'},
    @{name='Embedding unavailable';container='tm70-semantic-e2e-dev-embedding-1';operation='stop'},
    @{name='Qdrant unavailable';container='tm70-semantic-e2e-dev-qdrant-1';operation='stop'}
)) {
    $actual=docker inspect $case.container|ConvertFrom-Json|Select-Object -First 1
    $expected=$inventory|Where-Object name -eq $case.container
    if($actual.Id-cne$expected.id-or$actual.Config.Labels.'com.docker.compose.project'-cne'tm70-semantic-e2e-dev'){throw 'Container ownership mismatch.'}
    docker $case.operation $case.container | Out-Null
    if($LASTEXITCODE-ne0){throw 'Could not start controlled dependency failure.'}
    try {
        $r=Invoke-WebRequest 'http://127.0.0.1:5077/api/dev/tours/search?keyword=beach&mode=hybrid' -Headers @{'X-TM70-Dev-Key'=$key} -SkipHttpErrorCheck -TimeoutSec 30
        $content=if($r.Content-is[byte[]]){[Text.Encoding]::UTF8.GetString($r.Content)}else{$r.Content}
        $body=$content|ConvertFrom-Json
        $pass=$r.StatusCode-eq503-and$body.errorCode-eq'DevSearch.DependencyUnavailable'-and$body.traceId-and!$body.PSObject.Properties['items']
        $evidence+=@{scenario=$case.name;status=$(if($pass){'PASS'}else{'FAIL'});http=[int]$r.StatusCode;errorCode=$body.errorCode;traceId=$body.traceId;fallback=$false}
    } finally {
        if($case.operation-eq'pause'){docker unpause $case.container|Out-Null}else{docker start $case.container|Out-Null}
        $endpoint=if($case.container-like'*embedding*'){'http://127.0.0.1:8017/health'}else{'http://127.0.0.1:6337/readyz'}
        $ready=$false
        for($attempt=0;$attempt-lt40;$attempt++){
            try {Invoke-RestMethod $endpoint -TimeoutSec 1|Out-Null;$ready=$true;break}catch{Start-Sleep -Milliseconds 500}
        }
        if(!$ready){throw 'Owned dependency failed to recover; inspect its logs.'}
    }
}
$evidence|ConvertTo-Json -Depth 5|Set-Content "$RuntimeDirectory/../dependency-timeouts.json"
$evidence|ForEach-Object{"$($_.status) HTTP$($_.http) $($_.scenario) $($_.errorCode)"}
if(@($evidence|Where-Object status -eq FAIL).Count){throw 'Controlled dependency failures did not meet contract.'}
