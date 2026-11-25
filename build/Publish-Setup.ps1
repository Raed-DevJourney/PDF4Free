# Builds the downloadable installer: artifacts\PDF4Free-Setup.exe
#
# The installer is the application itself, published as a single self-contained file (the .NET runtime and every
# library are inside it, so the target PC needs nothing pre-installed). Started under the name "...Setup.exe" it
# shows the install page; the copy it installs is the same file, named PDF4Free.exe.
#
#   pwsh build/Publish-Setup.ps1 [-Runtime win-x64]

param([string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$stage = Join-Path $root 'artifacts\publish'
$setup = Join-Path $root 'artifacts\PDF4Free-Setup.exe'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish (Join-Path $root 'src\PDF4Free.App\PDF4Free.App.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item (Join-Path $stage 'PDF4Free.exe') $setup -Force
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host "Created $setup ($size MB)"
Write-Host "SHA-256: $hash"
