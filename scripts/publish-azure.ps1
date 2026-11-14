[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root "artifacts\azure\$([guid]::NewGuid().ToString('N'))"
$publish = Join-Path $output 'publish'

Push-Location (Join-Path $root 'web')
try {
    if (-not (Test-Path 'node_modules')) {
        & npm.cmd ci
        if ($LASTEXITCODE -ne 0) { throw 'Dashboard dependency installation failed.' }
    }

    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed.' }
}
finally {
    Pop-Location
}

& dotnet publish (Join-Path $root 'DroneSwarm.Web\DroneSwarm.Web.csproj') `
    --configuration Release --runtime linux-x64 --self-contained true --output $publish `
    --artifacts-path (Join-Path $output 'build') --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw 'ASP.NET Core publish failed.' }

if (-not (Test-Path (Join-Path $publish 'wwwroot\index.html'))) {
    throw 'The published dashboard is missing.'
}

$archive = Join-Path $output 'drone-swarm.zip'
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive
Write-Output "Deployment archive: $archive"
