<#
.SYNOPSIS
    Builds the dnSpy Analyzer CLI tool
#>
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$analyzerDir = Split-Path -Parent $PSScriptRoot

Write-Host "=== dnSpy Analyzer CLI Build ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow

Push-Location $analyzerDir
try {
    & dotnet build src/DnSpy.Analyzer.Core/DnSpy.Analyzer.Core.csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Core build failed" }
    Write-Host "DnSpy.Analyzer.Core OK" -ForegroundColor Green

    & dotnet build src/DnSpy.Analyzer.Cli/DnSpy.Analyzer.Cli.csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed" }
    Write-Host "analyzer CLI OK" -ForegroundColor Green
} finally {
    Pop-Location
}

Write-Host "`n=== Build complete ===" -ForegroundColor Cyan
Write-Host "Output: $analyzerDir\src\DnSpy.Analyzer.Cli\bin\$Configuration\net8.0\analyzer.exe" -ForegroundColor Cyan
