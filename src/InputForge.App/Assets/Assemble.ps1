$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot 'xbox_elite_controller.glb'
$temporary = $destination + '.tmp'
$segments = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'glb') -Filter '*.bin' | Sort-Object Name)
if ($segments.Count -ne 31) { throw 'The controller asset must contain all 31 ordered segments.' }
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::Create)
    try {
        foreach ($segment in $segments) {
            $source = [IO.File]::OpenRead($segment.FullName)
            try { $source.CopyTo($stream) } finally { $source.Dispose() }
        }
    } finally { $stream.Dispose() }
    $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
    if ($actual -ne '3C4E1DF10DA187716DDD8B787F5B6E1DD8298FD61E76010DB6EC857A9F1B0863') {
        throw 'Controller asset checksum mismatch.'
    }
    Move-Item -LiteralPath $temporary -Destination $destination -Force
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
