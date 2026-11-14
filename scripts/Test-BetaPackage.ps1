param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$core = Join-Path (Resolve-Path -LiteralPath $PackageDirectory).Path 'InputForge.Core.dll'
Add-Type -Path $core
$session = [InputForge.Core.InputSession]::new()
$profile = [InputForge.Core.Preferences]::Create()
$bindings = $profile.Bindings
$settings = $profile.Mouse
$results = [System.Collections.Generic.List[string]]::new()

function Check([bool]$Condition, [string]$Name) {
    $result = if ($Condition) { 'PASS' } else { 'FAIL' }
    $results.Add("- ${result}: $Name")
}

$session.SetScope([IntPtr]1, $bindings) | Out-Null
foreach ($key in @(87, 65, 32, 160, 256, 257)) { $session.SetButton($key, $true) }
$session.Move(12, -6, 100, 100) | Out-Null
$state = $session.Update($bindings, $settings, 0.008, 100)
$report = [InputForge.Core.ControllerReport]::new(0,0,0,0,0,0,0)
$encoded = [InputForge.Core.ControllerReport]::TryEncode($state, [ref]$report)
Check ($encoded -and $report.Buttons -eq 0x3000 -and $report.LX -lt 0 -and $report.LY -gt 0 -and
    $report.RX -gt 0 -and $report.RY -gt 0 -and $report.LT -eq 255 -and $report.RT -eq 255) 'W+A+Space+Shift, both triggers and diagonal mouse motion produce coherent Xbox report values'

$session.SetButton(32, $false)
$state = $session.Update($bindings, $settings, 0.008, 108)
Check ($state.XboxButtons -eq 0x2000 -and $state.LT -eq 1 -and $state.RT -eq 1 -and
    $state.Left.Y -gt 0) 'Releasing Space leaves Shift, both triggers and movement held'

$session.Move(-4000, 0, 125, 125) | Out-Null
$state = $session.Update($bindings, $settings, 0.008, 125)
Check ($state.Right.X -lt 0) 'A strong opposite mouse sample reverses the right stick'
foreach ($stamp in 133..450) {
    $state = $session.Update($bindings, $settings, 0.001, $stamp)
}
Check ($state.Right -eq [System.Numerics.Vector2]::Zero) 'Right stick returns to exact neutral after mouse motion stops'
$session.SetScope([IntPtr]0, $bindings) | Out-Null
$state = $session.Update($bindings, $settings, 0.008, 460)
Check ($state -eq [InputForge.Core.ControllerState]::new(0,[System.Numerics.Vector2]::Zero,[System.Numerics.Vector2]::Zero,0,0)) 'Leaving the input scope clears all held controls and axes in the shipped engine'

$results
if ($results -match '^- FAIL:') { throw 'The packaged mapping engine failed a smoke check.' }
