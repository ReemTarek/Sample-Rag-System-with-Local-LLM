# End-to-end check of the running API: upload -> index -> ask answerable questions -> ask an unanswerable one.
# Prereqs: Ollama running with both models, `docker compose up -d`, API running (dotnet run --project backend/MiniRag.Api).
param(
    [string]$Api = "http://localhost:5080",
    [string]$Sample = (Join-Path $PSScriptRoot "..\samples\contoso-handbook.pdf")
)
$ErrorActionPreference = "Stop"
$failures = 0

function Check($name, $ok, $detail = "") {
    if ($ok) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

function Ask($question) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod "$Api/api/chat" -Method Post -ContentType "application/json" `
        -Body (@{ question = $question } | ConvertTo-Json)
    Write-Host ("`nQ: {0}  ({1:n1}s)`nA: {2}" -f $question, $sw.Elapsed.TotalSeconds, $r.answer)
    foreach ($c in $r.citations) {
        Write-Host ("   [{0}] {1} p.{2} chunk {3} score {4}" -f $c.sourceNumber, $c.documentName, $c.pageNumber, $c.chunkIndex, $c.score)
    }
    return $r
}

Write-Host "== Health"
$h = Invoke-RestMethod "$Api/api/health"
Check "pipeline ready" $h.ready $h.error
Check "chat + embedding models available" ($h.ollama.chatModelAvailable -and $h.ollama.embeddingModelAvailable)
Write-Host "  embedding dimension: $($h.ollama.embeddingDimension)"

Write-Host "`n== Upload and index"
$null = & curl.exe -s -o NUL -X DELETE "$Api/api/documents/none"   # no-op, proves DELETE route is reachable
$upload = & curl.exe -s -w "`n%{http_code}" -F "file=@$Sample" "$Api/api/documents"
$code = ($upload -split "`n")[-1]; $doc = ($upload -split "`n")[0] | ConvertFrom-Json
Check "upload accepted (202)" ($code -eq "202")
$deadline = (Get-Date).AddMinutes(3)
do { Start-Sleep 1; $doc = (Invoke-RestMethod "$Api/api/documents") | Where-Object id -eq $doc.id } `
    while ($doc.status -in "Pending", "Indexing" -and (Get-Date) -lt $deadline)
Check "document indexed" ($doc.status -eq "Indexed") "status=$($doc.status) error=$($doc.error)"
Write-Host "  chunks: $($doc.chunkCount)"

Write-Host "`n== Answerable questions"
$r = Ask "How many vacation days do employees get per year?"
Check "answer mentions 27" ($r.answer -match "27")
Check "cites handbook page 3" (@($r.citations | Where-Object { $_.pageNumber -eq 3 -and $_.documentName -eq "contoso-handbook.pdf" }).Count -ge 1)

$r = Ask "What is the home office equipment stipend?"
Check "answer mentions 450" ($r.answer -match "450")
Check "cites handbook page 2" (@($r.citations | Where-Object { $_.pageNumber -eq 2 }).Count -ge 1)

$r = Ask "Who founded the company and where?"
Check "answer mentions Marta Okafor and Lisbon" (($r.answer -match "Okafor") -and ($r.answer -match "Lisbon"))
Check "cites handbook page 1" (@($r.citations | Where-Object { $_.pageNumber -eq 1 }).Count -ge 1)

Write-Host "`n== Unanswerable question"
$r = Ask "What is the CEO's favourite football team?"
Check "reports insufficient information" ($r.insufficientContext -eq $true)
Check "no citations" (@($r.citations).Count -eq 0)

Write-Host "`n== Validation"
$tmp = Join-Path ([IO.Path]::GetTempPath()) "sheet.xlsx"; "x" | Set-Content $tmp
$status = & curl.exe -s -o NUL -w "%{http_code}" -F "file=@$tmp" "$Api/api/documents"
Check "unsupported file type rejected (400)" ($status -eq "400") "got $status"
$emptyTxt = Join-Path ([IO.Path]::GetTempPath()) "empty.txt"; "   " | Set-Content $emptyTxt
$e = (& curl.exe -s -F "file=@$emptyTxt" "$Api/api/documents") | ConvertFrom-Json
Start-Sleep 3
$e = (Invoke-RestMethod "$Api/api/documents") | Where-Object id -eq $e.id
Check "whitespace-only file ends Failed, not Indexed" ($e.status -eq "Failed") "status=$($e.status)"
$null = & curl.exe -s -o NUL -X DELETE "$Api/api/documents/$($e.id)"

Write-Host "`n== Delete"
$status = & curl.exe -s -o NUL -w "%{http_code}" -X DELETE "$Api/api/documents/$($doc.id)"
Check "delete returns 204" ($status -eq "204") "got $status"
$r = Ask "How many vacation days do employees get per year?"
Check "after delete the answer is insufficient (no documents)" ($r.insufficientContext -eq $true)

if ($failures -gt 0) { Write-Host "`n$failures check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed" -ForegroundColor Green
