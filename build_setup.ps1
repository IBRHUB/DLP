param(
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistApp = Join-Path $ProjectRoot 'dist\app'
$SetupExe = Join-Path $ProjectRoot 'dist\installer\DLP_Setup.exe'
$SetupScript = Join-Path $ProjectRoot 'installer\DLP_Setup.iss'
$AppProject = Join-Path $ProjectRoot 'app\DLP.csproj'

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue

    if ($command) {
        return $command.Source
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:ProgramData\chocolatey\bin\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw 'ISCC.exe was not found. Install Inno Setup 6 first.'
}

Set-Location -LiteralPath $ProjectRoot

if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found. Install .NET 8 SDK first.'
}

if (-not (Test-Path -LiteralPath $SetupScript)) {
    throw "Missing Inno Setup script: $SetupScript"
}

if (-not $NoClean -and (Test-Path -LiteralPath $DistApp)) {
    Remove-Item -LiteralPath $DistApp -Recurse -Force
}

Write-Host 'Publishing DLP...'
dotnet publish $AppProject -c Release -r win-x64 --self-contained false

$iscc = Find-InnoCompiler

Write-Host 'Building installer...'
& $iscc $SetupScript

if (-not (Test-Path -LiteralPath $SetupExe)) {
    throw "Installer was not created: $SetupExe"
}

$file = Get-Item -LiteralPath $SetupExe
$sizeMb = [math]::Round($file.Length / 1MB, 2)

Write-Host ''
Write-Host "Done: $($file.FullName)"
Write-Host "Size: $sizeMb MB"
