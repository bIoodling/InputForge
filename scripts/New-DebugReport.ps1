param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$Commit
)

$ErrorActionPreference = 'Stop'
$results = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -ErrorAction SilentlyContinue)
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# InputForge debugging handoff')
$lines.Add('')
$lines.Add("Commit: $Commit")
$lines.Add('Scope: isolated Windows tests using synthetic inputs. Application launch, physical input and game compatibility are outside this run.')
$lines.Add('')
if ($results.Count -eq 0) {
    $lines.Add('**UNVERIFIED** — no test report was generated. Check the Windows CI build and test steps.')
}
foreach ($result in $results) {
    [xml]$document = Get-Content -LiteralPath $result.FullName -Raw
    $counter = $document.SelectSingleNode("//*[local-name()='Counters']")
    $lines.Add("Passed: $($counter.passed) | Failed: $($counter.failed) | Not executed: $($counter.notExecuted)")
    $lines.Add('')
    foreach ($test in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
        $lines.Add("- $($test.outcome) — $($test.testName)")
        $details = $test.SelectNodes(".//*[local-name()='Message' or local-name()='StdOut']")
        foreach ($detail in $details) {
            foreach ($line in $detail.InnerText.Trim().Split("`n")) {
                if ($line.Trim()) { $lines.Add("  - " + $line.Trim()) }
            }
        }
    }
}
$lines.Add('')
$smoke = Join-Path $ResultsDirectory 'package-smoke.md'
if (Test-Path -LiteralPath $smoke) {
    $lines.Add('## Packaged engine smoke checks')
    $lines.Add('')
    $lines.AddRange([string[]](Get-Content -LiteralPath $smoke))
    $lines.Add('')
}
$lines.Add('## Still to verify on Windows')
$lines.Add('')
$lines.Add('- Record whether the opt-in ViGEmBus/XInput test passed on a machine with the driver installed.')
$lines.Add('- In Testing Lab, check W+A+Space+Shift plus both mouse buttons, single-control release, fast reversal and return to neutral.')
$lines.Add('- With a permitted executable focused, check real virtual output, focus loss and Ctrl+Alt+Shift+F11.')
$lines.Add('')
$lines.Add('## Fix-request handoff')
$lines.Add('')
$lines.Add('Use failures and reproduced observations as evidence. For each manual finding include the build, steps, expected result, observed result and frequency. Treat unverified behavior as a test gap.')
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
Set-Content -LiteralPath $Output -Value $lines -Encoding utf8
