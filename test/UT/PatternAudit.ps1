# An exact TRX display name maps each behavioral pattern, not just the number of a Theory's rows.
function Write-UtPatternAudit($Trx,[string]$Inventory,[string]$PatternLedger,[string]$Destination,[switch]$RequireAllPatterns) {
    $functions = @(Import-Csv -LiteralPath $Inventory)
    $patterns = @(Import-Csv -LiteralPath $PatternLedger)
    if (!$functions.Count -or !$patterns.Count) { throw 'Expected a nonempty function inventory and pattern ledger.' }
    $functionsByKey = @{}
    foreach ($function in $functions) {
        $key = "$($function.source)|$($function.signature)"
        if (!$functionsByKey.ContainsKey($key)) { $functionsByKey[$key] = 0 }
        $functionsByKey[$key]++
    }
    $byFunction = @{}
    foreach ($pattern in $patterns) {
        $key = "$($pattern.source)|$($pattern.signature)"
        if ($functionsByKey[$key] -ne 1) { throw "UT pattern must identify exactly one declaration: $key" }
        if ($pattern.pattern_id -notmatch '^UTP-[A-Z0-9]+(-[A-Z0-9]+)+$' -or
            $pattern.set_reviewed -cnotin 'yes','no' -or !$pattern.expected -or !$pattern.trx_test_name) {
            throw "Invalid pattern row for $key."
        }
        if (!$byFunction.ContainsKey($key)) { $byFunction[$key] = [Collections.Generic.List[object]]::new() }
        $byFunction[$key].Add($pattern)
    }
    $duplicates = @($patterns | Group-Object pattern_id | Where-Object Count -ne 1)
    if ($duplicates.Count) { throw "UT pattern IDs must be unique: $($duplicates.Name -join ', ')." }

    $observed = [Collections.Generic.Dictionary[string,Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
    foreach ($row in $Trx.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
        $name = [string]$row.testName
        if (!$observed.ContainsKey($name)) { $observed[$name] = [Collections.Generic.List[string]]::new() }
        $observed[$name].Add([string]$row.outcome)
    }

    $rows = foreach ($function in $functions) {
        $key = "$($function.source)|$($function.signature)"
        $listed = $byFunction[$key]
        if (!$listed) {
            [pscustomobject]@{source=$function.source;signature=$function.signature;pattern_id='';expected='';
                trx_test_name='';pattern_result='unlisted';function_result='unreviewed'}
            continue
        }
        $review = @($listed | Select-Object -ExpandProperty set_reviewed -Unique)
        if ($review.Count -ne 1) { throw "Inconsistent review status for $key" }
        $cases = foreach ($item in $listed) {
            $outcomes = $observed[$item.trx_test_name]
            $result = if (!$outcomes) { 'missing' } elseif ($outcomes.Count -ne 1) { 'ambiguous' } else { $outcomes[0] }
            [pscustomobject]@{source=$function.source;signature=$function.signature;pattern_id=$item.pattern_id;
                expected=$item.expected;trx_test_name=$item.trx_test_name;pattern_result=$result;function_result=''}
        }
        $incomplete = @($cases | Where-Object pattern_result -cne 'Passed').Count -gt 0
        $verdict = if ($incomplete) { 'verification_failed' } elseif ($review[0] -eq 'yes') { 'reviewed_passed' } else { 'unreviewed' }
        foreach ($case in $cases) { $case.function_result = $verdict; $case }
    }
    $rows | Export-Csv -LiteralPath $Destination -NoTypeInformation -Encoding UTF8
    $reviewed = @($rows | Where-Object function_result -eq 'reviewed_passed' | Group-Object source,signature).Count
    $unverified = @($rows | Where-Object function_result -eq 'verification_failed').Count
    Write-Host "UT pattern sets reviewed and passing: $reviewed/$($functions.Count) declarations. See function-pattern-audit.csv."
    if ($unverified) { throw "$unverified listed UT pattern rows belong to unverified declarations." }
    if ($RequireAllPatterns -and $reviewed -ne $functions.Count) {
        throw "Full UT pattern coverage is not complete: $($functions.Count - $reviewed) declarations remain."
    }
}
