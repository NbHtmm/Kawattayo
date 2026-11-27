# Map reviewed behavioral assertions only, never equate executed lines with an asserted outcome.
function Write-UtFunctionTrace($Trx,[string]$Inventory,[string]$Mapping,[string]$Destination,[string]$BoundaryMapping,[string]$CaseLedger) {
    $functions = @(Import-Csv -LiteralPath $Inventory)
    $assertions = @(Import-Csv -LiteralPath $Mapping)
    if (!$functions.Count -or !$assertions.Count) { throw 'Expected a nonempty inventory and assertion map.' }

    $definitions = @{}
    foreach ($definition in $Trx.SelectNodes("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
        $method = $definition.SelectSingleNode("*[local-name()='TestMethod']")
        if ($method) { $definitions[$definition.id] = "$($method.className).$($method.name)" }
    }
    $results = @{}
    foreach ($result in $Trx.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
        $name = $definitions[$result.testId]
        if (!$name) { continue }
        if (!$results.ContainsKey($name)) { $results[$name] = [Collections.Generic.List[string]]::new() }
        $results[$name].Add($result.outcome)
    }

    $inventoryKeys = @{}
    foreach ($function in $functions) {
        $key = "$($function.source)|$($function.signature)"
        if (!$inventoryKeys.ContainsKey($key)) { $inventoryKeys[$key] = 0 }
        $inventoryKeys[$key]++
    }

    $links = @{}
    foreach ($link in $assertions) {
        $key = "$($link.source)|$($link.signature)"
        if ($inventoryKeys[$key] -ne 1) { throw "UT mapping must identify exactly one production signature: $key" }
        $method = "$($link.test_class).$($link.test_method)"
        if (!$link.test_class -or !$link.test_method -or $link.minimum_cases -notmatch '^[1-9][0-9]*$') {
            throw "Invalid test reference for $key."
        }
        if (!$links.ContainsKey($key)) { $links[$key] = @{} }
        if ($links[$key].ContainsKey($method)) { throw "Duplicate UT link: $key, $method." }
        $links[$key][$method] = [int]$link.minimum_cases
    }

    $boundaries = Read-UtSctBoundaryLinks $inventoryKeys $links $BoundaryMapping $CaseLedger
    $rows = foreach ($function in $functions) {
        $key = "$($function.source)|$($function.signature)"
        $linkGroup = $links[$key]
        $boundary = $boundaries[$key]
        $review = if ($boundary) { 'sct_linked' } else { 'not_mapped' }
        $testNames = ''
        $passed = 0
        $executed = 0
        if ($linkGroup) {
            $review = 'mapped_passed'
            $names = @($linkGroup.Keys | Sort-Object)
            $testNames = $names -join ';'
            foreach ($name in $names) {
                $outcomes = $results[$name]
                if ($outcomes) {
                    $executed += $outcomes.Count
                    $passed += @($outcomes | Where-Object { $_ -eq 'Passed' }).Count
                }
                if (!$outcomes -or $outcomes.Count -lt $linkGroup[$name] -or @($outcomes | Where-Object { $_ -ne 'Passed' }).Count) {
                    $review = 'mapped_not_verified'
                }
            }
        }
        [pscustomobject]@{
            source = $function.source; line = $function.line; signature = $function.signature; expected_test_file = $function.test_file
            assertion_tests = $testNames; verdict = $review; referenced_cases = $executed; passed_referenced_cases = $passed
            sct_case_ids = $boundary.case_ids; sct_scope = $boundary.scope
        }
    }
    $rows | Export-Csv -LiteralPath $Destination -NoTypeInformation -Encoding UTF8
    $passedLinks = @($rows | Where-Object verdict -eq 'mapped_passed').Count
    $unverified = @($rows | Where-Object verdict -eq 'mapped_not_verified').Count
    Write-Host "Reviewed UT assertions verified: $passedLinks/$($rows.Count) declarations. See function-trace.csv."
    Write-Host "External-boundary links (not UT passes): $($boundaries.Count); remaining declarations without links: $(@($rows | Where-Object verdict -eq 'not_mapped').Count)."
    if ($unverified) { throw "$unverified mapped UT declarations lack passing results for their referenced cases." }
}
