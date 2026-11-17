[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Repository,
    [Parameter(Mandatory)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path -LiteralPath $Repository).ProviderPath
$destinationPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if (Test-Path -LiteralPath $destinationPath) {
    throw 'Choose a new directory for Destination.'
}
if (!(Test-Path -LiteralPath (Join-Path $repoPath 'src/server/server.ts')) -or
    !(Test-Path -LiteralPath (Join-Path $repoPath 'protobufs/meshtastic/mesh.proto'))) {
    throw 'Use a MeshMonitor checkout with its Git submodules initialized.'
}

$nodeExe = (Get-Command node.exe -CommandType Application | Select-Object -First 1).Source
$npm = (Get-Command npm.cmd -CommandType Application | Select-Object -First 1).Source
$nodeVersion = & $nodeExe -p 'process.platform + "/" + process.arch + "/" + process.versions.node.split(".")[0]'
if ($LASTEXITCODE -ne 0 -or $nodeVersion -ne 'win32/x64/24') {
    throw 'Install x64 Node.js 24 with npm, matching the upstream Windows build.'
}

function Invoke-Npm {
    & $npm @args
    if ($LASTEXITCODE -ne 0) { throw "npm failed: $($args -join ' ')" }
}

$previousNodeOptions = $env:NODE_OPTIONS
Push-Location -LiteralPath $repoPath
try {
    $env:NODE_OPTIONS = '--max-old-space-size=4096'
    Invoke-Npm ci --legacy-peer-deps
    Invoke-Npm run build
    Invoke-Npm exec -- tsc --project tsconfig.server.json

    $dist = Join-Path $destinationPath 'dist'
    $binaries = Join-Path $destinationPath 'binaries'
    New-Item -ItemType Directory -Path $destinationPath | Out-Null
    New-Item -ItemType Directory -Path $dist, $binaries | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repoPath 'dist') -Force |
        Copy-Item -Destination $dist -Recurse
    Copy-Item -LiteralPath 'protobufs' -Destination $dist -Recurse
    Copy-Item -LiteralPath 'package.json' -Destination $destinationPath
    Copy-Item -LiteralPath 'package.json', 'package-lock.json' -Destination $dist
    Copy-Item -LiteralPath 'src/server/routes/v1/openapi.yaml' -Destination (Join-Path $dist 'server/routes/v1')
    Copy-Item -LiteralPath $nodeExe -Destination (Join-Path $binaries 'node.exe')
    Set-Location -LiteralPath $dist
    Invoke-Npm ci --omit=dev --legacy-peer-deps --no-fund
}
finally {
    $env:NODE_OPTIONS = $previousNodeOptions
    Pop-Location
}

Write-Output $destinationPath
