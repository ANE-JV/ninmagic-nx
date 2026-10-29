param([string]$Dotnet = 'dotnet', [string]$OutputRoot = 'dist')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $resolvedOutputRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $projectRoot $OutputRoot }
    $publishFolder = Join-Path $resolvedOutputRoot 'win-x64'
    & $Dotnet run --project tests/Ninmagic.Nx.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Las pruebas no han pasado. No se publica un ejecutable.' }
    & $Dotnet run --project tools/RenderUi -c Release -- (Join-Path $resolvedOutputRoot 'ui-checks')
    if ($LASTEXITCODE -ne 0) { throw 'Las comprobaciones de interfaz no han pasado. No se publica un ejecutable.' }
    & $Dotnet publish src/Ninmagic.Nx.App/Ninmagic.Nx.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=embedded -o $publishFolder --nologo
    if ($LASTEXITCODE -ne 0) { throw 'La compilación no se ha completado.' }
    [xml]$buildProperties = Get-Content -LiteralPath 'Directory.Build.props'
    $version = $buildProperties.Project.PropertyGroup.Version
    $package = Join-Path $resolvedOutputRoot "ninmagic-nx-$version-windows-x64"
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishFolder 'ninmagic-nx.exe') -Destination $package
    Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $package 'LEEME.md')
    Copy-Item -LiteralPath 'docs/VALIDACION.md' -Destination $package
    $digest = (Get-FileHash -LiteralPath (Join-Path $package 'ninmagic-nx.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $package 'SHA256SUMS.txt'), "$digest  ninmagic-nx.exe`n", [Text.UTF8Encoding]::new($false))
    Compress-Archive -LiteralPath (Join-Path $package 'ninmagic-nx.exe'),(Join-Path $package 'LEEME.md'),(Join-Path $package 'VALIDACION.md'),(Join-Path $package 'SHA256SUMS.txt') -DestinationPath (Join-Path $resolvedOutputRoot "ninmagic-nx-$version-windows-x64.zip") -Force
    Write-Output "Portable listo. SHA256: $digest"
} finally { Pop-Location }
