# Builds the Rolling Candle indicator on its own and installs it where Quantower looks for indicators.
#   .\deploy.ps1
# With ScalpFeed installed, skip this: its KeyMovingAverages.dll already has the same indicator, and two copies would
# both show in Quantower's list.
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
$project = Join-Path $PSScriptRoot 'src\RollingCandle.csproj'
$output = Join-Path $PSScriptRoot 'src\bin\Release\RollingCandle.dll'
$targetDir = 'C:\Quantower\Settings\Scripts\Indicators\RollingCandle'
$target = Join-Path $targetDir 'RollingCandle.dll'

& $dotnet build $project -c Release -v quiet -nologo --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was deployed.' }

New-Item -ItemType Directory -Force $targetDir | Out-Null
Get-ChildItem $targetDir -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item $_.FullName -Force -Confirm:$false } catch { }
}

# A running Quantower may hold the DLL open; Windows still allows a rename.
if (Test-Path $target) {
    try {
        Copy-Item $output $target -Force
        "Installed over the existing DLL."
    }
    catch {
        Rename-Item $target "RollingCandle.$(Get-Date -Format yyyyMMddHHmmss).old"
        Copy-Item $output $target
        "Quantower has the old DLL open. Parked it; the new one loads when Quantower restarts."
    }
}
else {
    Copy-Item $output $target
    "Installed."
}
