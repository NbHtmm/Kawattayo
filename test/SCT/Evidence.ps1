function Assert-SctScenarioEvidence {
    param([string]$Path, [string]$CaseId)

    # A success exit code is not proof that the required scenario and all its steps ran.
    $records = @(Get-Content -LiteralPath $Path -Encoding UTF8 |
        Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { ConvertFrom-Json -InputObject $_ -ErrorAction Stop })
    $runs = @($records | Where-Object testRunStarted | ForEach-Object testRunStarted)
    $runEnds = @($records | Where-Object testRunFinished | ForEach-Object testRunFinished)
    if ($runs.Count -ne 1 -or $runEnds.Count -ne 1 -or $runEnds[0].success -ne $true -or
        $runEnds[0].testRunStartedId -ne $runs[0].id) {
        throw "Incomplete or unsuccessful Cucumber run: $CaseId."
    }

    $pickles = @($records | Where-Object pickle | ForEach-Object pickle |
        Where-Object { $_.tags.name -ccontains ('@' + $CaseId) })
    if ($pickles.Count -ne 1 -or !$pickles[0].steps) {
        throw "Required scenario is missing, ambiguous or empty: $CaseId."
    }

    $cases = @($records | Where-Object testCase | ForEach-Object testCase |
        Where-Object { $_.pickleId -eq $pickles[0].id })
    $starts = @($records | Where-Object testCaseStarted | ForEach-Object testCaseStarted)
    if ($cases.Count -ne 1 -or $starts.Count -ne 1 -or $starts[0].testCaseId -ne $cases[0].id) {
        throw "Required scenario did not run exactly once: $CaseId."
    }
    $startId = $starts[0].id
    $ends = @($records | Where-Object testCaseFinished | ForEach-Object testCaseFinished |
        Where-Object { $_.testCaseStartedId -eq $startId })
    $results = @($records | Where-Object testStepFinished | ForEach-Object testStepFinished |
        Where-Object { $_.testCaseStartedId -eq $startId })
    if ($ends.Count -ne 1 -or $ends[0].willBeRetried -ne $false -or $results.Count -ne @($cases[0].testSteps).Count) {
        throw "Incomplete scenario evidence: $CaseId."
    }
    foreach ($step in $cases[0].testSteps) {
        $found = @($results | Where-Object { $_.testStepId -eq $step.id })
        if ($found.Count -ne 1 -or $found[0].testStepResult.status -cne 'PASSED') {
            throw "Step lacks a unique passing result: $CaseId / $($step.id)."
        }
    }
    foreach ($step in $pickles[0].steps) {
        $linked = @($cases[0].testSteps | Where-Object { $_.pickleStepId -eq $step.id })
        if ($linked.Count -ne 1 -or !$linked[0].stepDefinitionIds -or @($linked[0].stepDefinitionIds).Count -ne 1) {
            throw "Step lacks a unique binding: $CaseId / $($step.text)."
        }
    }
}
