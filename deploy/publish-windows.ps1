# Builds the Windows app as one self-contained exe (no .NET needed on the PC).
# Usage: deploy/publish-windows.ps1 [-VersionSuffix ci.42]
# Result: artifacts/PacketBot-Windows-<version>-win-x64.zip with
# PacketBot.Windows.exe, Navigation/maps.json and the licence.
param([string]$VersionSuffix = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.VersionPrefix
if ($VersionSuffix) { $version = "$version-$VersionSuffix" }

$out = Join-Path $root "artifacts/packetbot-windows"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$publish = @(
    "publish", (Join-Path $root "PacketBot.Windows/PacketBot.Windows.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", $out)
if ($VersionSuffix) { $publish += "-p:VersionSuffix=$VersionSuffix" }
dotnet @publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item (Join-Path $root "License") (Join-Path $out "License.txt")

$zip = Join-Path $root "artifacts/PacketBot-Windows-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Output "Listo: $zip"
