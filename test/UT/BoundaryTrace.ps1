# Keep OS/GUI/IO boundary links separate from passing UT. A link is not a passing SCT.
function Read-UtSctBoundaryLinks($InventoryKeys,$UnitLinks,[string]$Mapping,[string]$CaseLedger) {
    $ledger = Read-SctCaseLedger $CaseLedger
    $boundaries = @{}
    foreach ($link in (Import-Csv -LiteralPath $Mapping)) {
        $key = "$($link.source)|$($link.signature)"
        if ($InventoryKeys[$key] -ne 1) {
            throw "Boundary mapping must identify exactly one production declaration: $key."
        }
        if ($UnitLinks.ContainsKey($key)) {
            throw "Keep a declaration's primary UT and external-boundary mappings disjoint: $key."
        }
        if ($boundaries.ContainsKey($key)) { throw "Duplicate external-boundary mapping: $key." }
        $caseIds = @($link.case_ids -split ';')
        $unique = @($caseIds | Sort-Object -Unique)
        if (!$link.scope -or !$link.case_ids -or $unique.Count -ne $caseIds.Count) {
            throw "Boundary requires a scope and distinct SCT cases: $key."
        }
        foreach ($id in $caseIds) {
            if ($id -cnotmatch '^SCT-[A-Z]+-\d{3}$' -or !$ledger.ContainsKey($id)) {
                throw "Unknown SCT case in boundary mapping: $key, $id."
            }
        }
        $boundaries[$key] = $link
    }
    return $boundaries
}
