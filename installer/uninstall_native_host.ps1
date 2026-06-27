[CmdletBinding()]
param(
    [ValidateSet('chrome', 'edge', 'brave', 'both', 'all')]
    [string]$Browser = 'all',

    [switch]$RemoveGeneratedManifest
)

$ErrorActionPreference = 'Stop'
$HostName = 'com.ibrhub.dlp'

$browserKeys = @()

if ($Browser -eq 'chrome' -or $Browser -eq 'both' -or $Browser -eq 'all') {
    $browserKeys += "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
}

if ($Browser -eq 'edge' -or $Browser -eq 'both' -or $Browser -eq 'all') {
    $browserKeys += "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"
}

if ($Browser -eq 'brave' -or $Browser -eq 'all') {
    $browserKeys += "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName"
}

foreach ($key in $browserKeys) {
    if (Test-Path -LiteralPath $key) {
        Remove-Item -LiteralPath $key -Force
    }
}

$appRegistryKey = 'HKCU:\Software\IBRHUB\DLP'
if (Test-Path -LiteralPath $appRegistryKey) {
    Remove-ItemProperty -LiteralPath $appRegistryKey -Name 'AppPath' -ErrorAction SilentlyContinue

    $remainingValues = Get-ItemProperty -LiteralPath $appRegistryKey
    $remainingUserValues = $remainingValues.PSObject.Properties |
        Where-Object { $_.Name -notin @('PSPath', 'PSParentPath', 'PSChildName', 'PSDrive', 'PSProvider') }

    if (-not $remainingUserValues) {
        Remove-Item -LiteralPath $appRegistryKey -Force
    }
}

if ($RemoveGeneratedManifest) {
    $manifestPath = Join-Path $env:LOCALAPPDATA "DLP\native-host\$HostName.json"

    if (Test-Path -LiteralPath $manifestPath) {
        Remove-Item -LiteralPath $manifestPath -Force
    }
}

Write-Host "Uninstalled DLP native messaging host for: $Browser"
