<#
.SYNOPSIS
    Publish StickyTodo.Avalonia as self-contained portable packages for Windows 7 SP1 (x64 + x86).

.DESCRIPTION
    Produces:
      publish-avalonia/StickyTodo-Avalonia-<version>-win-x64-portable.zip
      publish-avalonia/StickyTodo-Avalonia-<version>-win-x86-portable.zip

    Each zip contains:
      StickyTodo.Avalonia.exe   (single-file, self-contained .NET 6 runtime)
      README.txt
      portable.marker

    After publishing, each exe is smoke-tested through its --selftest entry point
    (checks runtime startup, data directory and SQLite native library).

.PARAMETER Version
    Version number, e.g. 1.0.0. Written to the exe metadata and zip file name.

.PARAMETER OutputDirectory
    Output folder, default: publish-avalonia.

.PARAMETER SkipTests
    Skip dotnet test on the solution.

.PARAMETER SkipSmoke
    Skip the --selftest smoke run for each published exe.

.PARAMETER NuGetSource
    Optional NuGet source used for restore, e.g. a mirror when nuget.org is blocked.

.EXAMPLE
    .\tools\publish-avalonia-win7.ps1 -Version 1.0.0

.EXAMPLE
    .\tools\publish-avalonia-win7.ps1 -Version 1.0.0 -NuGetSource https://repo.huaweicloud.com/repository/nuget/v3/index.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $OutputDirectory = "publish-avalonia",

    [switch] $SkipTests,

    [switch] $SkipSmoke,

    [string] $NuGetSource = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repoRoot "StickyTodo.sln"))) {
    $repoRoot = $PSScriptRoot
}
if (-not (Test-Path (Join-Path $repoRoot "StickyTodo.sln"))) {
    throw "Cannot find StickyTodo.sln. Run this script from the repository."
}

$project = Join-Path $repoRoot "src\StickyTodo.Avalonia\StickyTodo.Avalonia.csproj"
if (-not (Test-Path $project)) {
    throw "Cannot find $project"
}

$template = Get-Content (Join-Path $PSScriptRoot "win7-portable-readme.txt") -Raw -Encoding UTF8

$targets = @(
    [pscustomobject]@{ Rid = "win-x64"; Arch = "64 位" },
    [pscustomobject]@{ Rid = "win-x86"; Arch = "32 位" }
)

Push-Location $repoRoot
try {
    Write-Host "==> Repository : $repoRoot" -ForegroundColor Cyan
    Write-Host "==> Version    : $Version" -ForegroundColor Cyan

    if (-not $SkipTests) {
        Write-Host "==> Running unit tests..." -ForegroundColor Cyan
        dotnet test StickyTodo.sln --nologo -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) {
            throw "Unit tests failed."
        }
    }

    $publishDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    foreach ($target in $targets) {
        $rid = $target.Rid
        $stageDir = Join-Path $publishDir "stage-$rid"
        Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

        Write-Host "==> Publishing $rid (self-contained, single-file)..." -ForegroundColor Cyan

        $publishArgs = @(
            "publish", $project,
            "-c", "Release",
            "-r", $rid,
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true",
            "-p:DebugType=embedded",
            "-p:NuGetAudit=false",
            "-p:Version=$Version",
            "-p:AssemblyVersion=$Version",
            "-p:FileVersion=$Version",
            "-o", $stageDir
        )
        if (-not [string]::IsNullOrWhiteSpace($NuGetSource)) {
            $publishArgs += @("--source", $NuGetSource)
        }

        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed for $rid."
        }

        $exe = Join-Path $stageDir "StickyTodo.Avalonia.exe"
        if (-not (Test-Path $exe)) {
            throw "Publish did not produce StickyTodo.Avalonia.exe for $rid."
        }

        # Single-file publish should only leave the exe. Drop anything else
        # (pdb, deps.json, ...) so the portable package stays clean.
        Get-ChildItem $stageDir -File |
            Where-Object { $_.Name -ne "StickyTodo.Avalonia.exe" } |
            Remove-Item -Force

        "portable" | Set-Content (Join-Path $stageDir "portable.marker") -Encoding ASCII -NoNewline

        $readme = $template.Replace("{{VERSION}}", $Version).Replace("{{ARCH}}", $target.Arch).Replace("{{RID}}", $rid)
        Set-Content (Join-Path $stageDir "README.txt") -Value $readme -Encoding UTF8

        $zipName = "StickyTodo-Avalonia-$Version-$rid-portable.zip"
        $zipPath = Join-Path $publishDir $zipName
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

        $exeSizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        $zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
        $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash

        Write-Host "    exe : $exe ($exeSizeMb MB)" -ForegroundColor Green
        Write-Host "    zip : $zipPath ($zipSizeMb MB)" -ForegroundColor Green
        Write-Host "    sha : $hash" -ForegroundColor Green

        if (-not $SkipSmoke) {
            Write-Host "==> Smoke test $rid (--selftest)..." -ForegroundColor Cyan
            $smokeDir = Join-Path $publishDir "smoke-$rid"
            Remove-Item $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
            Copy-Item $exe $smokeDir

            $oldDataDir = $env:STICKYTODO_DATA_DIR
            $env:STICKYTODO_DATA_DIR = Join-Path $smokeDir "data"
            try {
                $process = Start-Process -FilePath (Join-Path $smokeDir "StickyTodo.Avalonia.exe") `
                    -ArgumentList "--selftest" `
                    -WorkingDirectory $smokeDir `
                    -PassThru -Wait

                if ($process.ExitCode -ne 0) {
                    throw "Smoke test failed for $rid (exit code $($process.ExitCode))."
                }

                $report = Join-Path $smokeDir "avalonia-selftest.txt"
                if (-not (Test-Path $report)) {
                    throw "Smoke test did not produce avalonia-selftest.txt for $rid."
                }

                $text = Get-Content $report -Raw -Encoding UTF8
                if ($text -notmatch "RESULT\s+:\s+OK") {
                    throw "Smoke test report is not OK for ${rid}:`n$text"
                }

                Write-Host "    smoke OK" -ForegroundColor Green
            }
            finally {
                if ($null -eq $oldDataDir) {
                    Remove-Item Env:\STICKYTODO_DATA_DIR -ErrorAction SilentlyContinue
                }
                else {
                    $env:STICKYTODO_DATA_DIR = $oldDataDir
                }
                Remove-Item $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host ""
    Write-Host "Done. Portable packages are in $publishDir" -ForegroundColor Green
}
finally {
    Pop-Location
}