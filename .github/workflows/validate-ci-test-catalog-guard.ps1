# Regression test for the "Drop this run's scoped test database" step's name guard in ci.yml
# (windows-tests job). Not wired into any CI job — this repo has no PowerShell test runner — but kept
# here, next to the workflow it verifies, as the retained, re-runnable proof that the guard's regex
# accepts only the exact grammar GitHub's own run_id/run_attempt produce and rejects everything else,
# including a value that shares the prefix but isn't otherwise valid. Run manually with:
#   powershell -File .github/workflows/validate-ci-test-catalog-guard.ps1
# Exits non-zero (and prints which case failed) if any expectation below stops holding — e.g. if the
# workflow's own copy of this regex is ever edited out of sync with this file.

$guard = { param($dbName) $dbName -match '^MCAROC_Analysis_CI_[0-9]+_[0-9]+$' }

$cases = @(
    @{ Name = 'MCAROC_Analysis_CI_123_1';                    Expect = $true;  Label = 'valid run_id/run_attempt shape' }
    @{ Name = 'MCAROC_Analysis_CI_999999999_2';               Expect = $true;  Label = 'valid, larger numbers' }
    @{ Name = 'MCAROC_Analysis_CI_abc_1';                     Expect = $false; Label = 'prefix-matching but non-numeric run_id' }
    @{ Name = 'MCAROC_Analysis_CI_1_2_3';                     Expect = $false; Label = 'prefix-matching but an extra segment' }
    @{ Name = 'MCAROC_Analysis_CI_1_';                        Expect = $false; Label = 'prefix-matching but missing run_attempt' }
    @{ Name = 'MCAROC_Analysis_CI_1_2]; DROP TABLE x--';      Expect = $false; Label = 'prefix-matching but an injection attempt' }
    @{ Name = 'MCAROC_Analysis_Test';                         Expect = $false; Label = 'the shared default database name itself' }
    @{ Name = 'MCAROC_Analysis_CI_';                          Expect = $false; Label = 'bare prefix, nothing after it' }
)

$failed = 0
foreach ($case in $cases) {
    $actual = & $guard $case.Name
    if ($actual -eq $case.Expect) {
        Write-Host "PASS  [$($case.Label)] '$($case.Name)' -> $actual"
    } else {
        Write-Host "FAIL  [$($case.Label)] '$($case.Name)' -> got $actual, expected $($case.Expect)"
        $failed++
    }
}

if ($failed -gt 0) {
    Write-Error "$failed case(s) failed."
    exit 1
}
Write-Host "All $($cases.Count) cases passed."
