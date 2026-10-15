# Publishes a self-contained build and compiles the Inno Setup installer.
# Output: artifacts\installer\DisplaySwitcher-Setup-<version>.exe
# Needs the .NET 8+ SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'src\DisplaySwitcher.csproj'))).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe not found; install Inno Setup 6' }

$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Self-contained: the target PC needs neither the .NET Desktop nor the ASP.NET Core runtime.
dotnet publish (Join-Path $root 'src') -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\DisplaySwitcher.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Get-Item (Join-Path $root "artifacts\installer\DisplaySwitcher-Setup-$Version.exe")
