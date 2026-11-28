# Keep the run complete: a missing, duplicate, or unexpected automatic case is a runner failure.
function Read-SctCaseLedger([string]$Path) {
    $status = @{}
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if ($line -notmatch '^\|\s*SCT-') { continue }
        if ($line -notmatch '^\|\s*(SCT-[A-Z]+-\d{3})\s*\|.*\|\s*(自動|予定|要環境)\s*\|\s*$') {
            throw "Invalid SCT ledger row: $line"
        }
        $id, $kind = $Matches[1], $Matches[2]
        if ($status.ContainsKey($id)) { throw "Duplicate SCT ledger case: $id." }
        $status.Add($id,$kind)
    }
    if ($status.Count -eq 0 -or '自動' -notin $status.Values) { throw 'No automatic SCT cases in ledger.' }
    return $status
}

function Assert-SctRunMatchesLedger($Ledger,$Results) {
    $expected = @($Ledger.Keys | Where-Object { $Ledger[$_] -eq '自動' })
    $ids = @($Results | ForEach-Object { $_.id })
    $missing = @($expected | Where-Object { $_ -cnotin $ids })
    $extra = @($ids | Where-Object { $_ -cnotin $expected })
    $duplicates = @($ids | Group-Object -CaseSensitive | Where-Object Count -gt 1 | ForEach-Object Name)
    if ($missing.Count -or $extra.Count -or $duplicates.Count) {
        throw "SCT ledger mismatch: missing [$($missing -join ', ')]; unexpected [$($extra -join ', ')]; duplicate [$($duplicates -join ', ')]."
    }
}
