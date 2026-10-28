param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & $Dotnet run --project tests/Ninmagic.Nx.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Las pruebas no han pasado. No se publica un ejecutable.' }
    & $Dotnet publish src/Ninmagic.Nx.App/Ninmagic.Nx.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=embedded -o dist/win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'La compilación no se ha completado.' }
    $package = Join-Path $projectRoot 'dist/ninmagic-nx-1.0.0-windows-x64'
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    Copy-Item -LiteralPath 'dist/win-x64/ninmagic-nx.exe' -Destination $package
    Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $package 'LEEME.md')
    Copy-Item -LiteralPath 'docs/VALIDACION.md' -Destination $package
    $digest = (Get-FileHash -LiteralPath (Join-Path $package 'ninmagic-nx.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $package 'SHA256SUMS.txt'), "$digest  ninmagic-nx.exe`n", [Text.UTF8Encoding]::new($false))
    Compress-Archive -LiteralPath (Join-Path $package 'ninmagic-nx.exe'),(Join-Path $package 'LEEME.md'),(Join-Path $package 'VALIDACION.md'),(Join-Path $package 'SHA256SUMS.txt') -DestinationPath 'dist/ninmagic-nx-1.0.0-windows-x64.zip' -Force
    Write-Output "Portable listo. SHA256: $digest"
} finally { Pop-Location }
