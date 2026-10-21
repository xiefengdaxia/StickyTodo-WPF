<#
.SYNOPSIS
    把 StickyTodo 打包成单文件便携版（win-x64），并可选地创建/更新 GitHub Release。

.DESCRIPTION
    产物是一个自包含的单 exe（约 70MB，内含 .NET 8 运行时），目标机器不需要安装任何运行时。

    便携模式的判定：exe 同目录存在 `portable.marker` 时，数据（sticky.db + logs）写在 exe 旁边的 `data\` 里。
    ZIP 包里已经放了 marker，所以解压即用、拷走整个文件夹就能带走全部数据。

.PARAMETER Version
    版本号，例如 1.0.0。会写进 exe 的文件版本信息，也用于 Release 的 tag 和文件名。

.PARAMETER OutputDirectory
    产物输出目录，默认 ./publish。

.PARAMETER SkipTests
    跳过 dotnet test（CI 里已经跑过时可以跳过）。

.PARAMETER CreateRelease
    打包完成后调用 GitHub API 创建 Release 并上传 ZIP。
    需要环境变量 GITHUB_TOKEN 或 GitHubToken 参数。

.PARAMETER GitHubToken
    GitHub Personal Access Token（需要 repo 权限）。不给就读 GITHUB_TOKEN 环境变量。

.PARAMETER Repository
    owner/repo，默认 xiefengdaxia/StickyTodo-WPF。

.EXAMPLE
    .\tools\publish-portable.ps1 -Version 1.0.0

.EXAMPLE
    .\tools\publish-portable.ps1 -Version 1.0.0 -CreateRelease -GitHubToken $env:GITHUB_TOKEN
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $OutputDirectory = "publish",

    [switch] $SkipTests,

    [switch] $CreateRelease,

    [string] $GitHubToken = $env:GITHUB_TOKEN,

    [string] $Repository = "xiefengdaxia/StickyTodo-WPF"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# --- 定位仓库根目录（脚本可能在 tools/ 下） ---
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repoRoot "StickyTodo.sln"))) {
    $repoRoot = $PSScriptRoot
}
if (-not (Test-Path (Join-Path $repoRoot "StickyTodo.sln"))) {
    throw "找不到 StickyTodo.sln，请在仓库根目录或 tools/ 下运行本脚本"
}

Push-Location $repoRoot
try {
    Write-Host "==> 仓库根目录: $repoRoot" -ForegroundColor Cyan
    Write-Host "==> 目标版本: $Version" -ForegroundColor Cyan

    # --- 1. 编译与测试 ---
    if (-not $SkipTests) {
        Write-Host "==> 运行单元测试..." -ForegroundColor Cyan
        dotnet test StickyTodo.sln --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "单元测试未通过，终止打包"
        }
    }

    # --- 2. 单文件发布 ---
    $publishDir = Join-Path $repoRoot $OutputDirectory
    $stageDir = Join-Path $publishDir "stage"
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    Write-Host "==> 单文件发布（win-x64, self-contained）..." -ForegroundColor Cyan
    dotnet publish "src\StickyTodo.App\StickyTodo.App.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -p:Version=$Version `
        -p:AssemblyVersion=$Version `
        -p:FileVersion=$Version `
        -o $stageDir

    if ($LASTEXITCODE -ne 0) {
        throw "发布失败"
    }

    $exe = Join-Path $stageDir "StickyTodo.exe"
    if (-not (Test-Path $exe)) {
        throw "没有生成 StickyTodo.exe"
    }

    # 只保留 exe（单文件发布理论上只有它，保险起见清理其它残留）
    Get-ChildItem $stageDir -File | Where-Object { $_.Name -ne "StickyTodo.exe" } | Remove-Item -Force

    # --- 3. 放入便携标记与说明 ---
    "portable" | Set-Content (Join-Path $stageDir "portable.marker") -Encoding UTF8 -NoNewline

    $readme = @"
StickyTodo $Version — 便携版 / Portable
=========================================

双击 StickyTodo.exe 即可运行，不需要安装 .NET 运行时。

【便携模式】
本目录里的 portable.marker 会让程序把数据存在旁边的 data\ 文件夹里：
    data\sticky.db     所有看板与待办
    data\logs\         运行日志
把整个文件夹拷到 U 盘或另一台电脑，数据会一起带走。
删掉 portable.marker 就回到常规模式（数据存到 %LOCALAPPDATA%\StickyTodo）。

【首次运行可能被拦】
这是未签名的开源程序，Windows SmartScreen 可能提示"已保护你的电脑"。
点「更多信息」→「仍要运行」即可。源码全部公开，可自行编译核对。

【系统要求】
Windows 10 1809 (x64) 或更高版本。

【许可证】
MIT。源码：https://github.com/$Repository
"@
    Set-Content (Join-Path $stageDir "README.txt") -Value $readme -Encoding UTF8

    # --- 4. 打 ZIP ---
    $zipName = "StickyTodo-$Version-win-x64-portable.zip"
    $zipPath = Join-Path $publishDir $zipName
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

    Write-Host "==> 打包 ZIP..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

    $exeSizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash

    Write-Host ""
    Write-Host "==> 打包完成" -ForegroundColor Green
    Write-Host "    exe : $exe  ($exeSizeMb MB)"
    Write-Host "    zip : $zipPath  ($zipSizeMb MB)"
    Write-Host "    sha : $hash"

    # --- 5. 冒烟测试：便携模式自检 ---
    Write-Host "==> 冒烟测试（--portable-check）..." -ForegroundColor Cyan
    $smokeDir = Join-Path $publishDir "smoke"
    Remove-Item $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
    Copy-Item $exe $smokeDir
    "portable" | Set-Content (Join-Path $smokeDir "portable.marker") -Encoding UTF8 -NoNewline

    $oldDataDir = $env:STICKYTODO_DATA_DIR
    Remove-Item Env:\STICKYTODO_DATA_DIR -ErrorAction SilentlyContinue
    try {
        $proc = Start-Process -FilePath (Join-Path $smokeDir "StickyTodo.exe") `
            -ArgumentList "--portable-check" -PassThru -Wait
        if ($proc.ExitCode -ne 0) {
            throw "冒烟测试失败（退出码 $($proc.ExitCode)），便携模式可能不可用"
        }

        if (-not (Test-Path (Join-Path $smokeDir "data\sticky.db"))) {
            throw "冒烟测试失败：便携模式没有在 exe 旁边创建 data\sticky.db"
        }

        Write-Host "    便携模式正常：数据落在 exe 旁边的 data\ 里" -ForegroundColor Green
    }
    finally {
        if ($oldDataDir) { $env:STICKYTODO_DATA_DIR = $oldDataDir }
        Remove-Item $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # --- 6. 可选：创建 GitHub Release ---
    if ($CreateRelease) {
        if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
            throw "创建 Release 需要 GitHubToken（或设置 GITHUB_TOKEN 环境变量）"
        }

        Write-Host "==> 创建 GitHub Release v$Version ..." -ForegroundColor Cyan

        $headers = @{
            Authorization          = "token $GitHubToken"
            Accept                 = "application/vnd.github+json"
            "User-Agent"           = "StickyTodo-publish-script"
        }

        $bodyJson = @{
            tag_name         = "v$Version"
            name             = "StickyTodo $Version"
            draft            = $false
            prerelease       = $false
            generate_release_notes = $true
            body             = @"
## StickyTodo $Version（便携版）

单文件便携版，**不需要安装 .NET 运行时**。下载 ZIP 解压后双击 ``StickyTodo.exe`` 即可运行。

- 数据默认存在 exe 旁边的 ``data\`` 里（zip 内含 ``portable.marker``），拷走整个文件夹即可带走数据
- 首次运行若被 SmartScreen 拦截，点「更多信息」→「仍要运行」
- 系统要求：Windows 10 1809 (x64) 或更高
- SHA256: ``$hash``

---
"@
        } | ConvertTo-Json -Depth 5

        $release = Invoke-RestMethod -Method Post `
            -Uri "https://api.github.com/repos/$Repository/releases" `
            -Headers $headers `
            -Body $bodyJson `

        Write-Host "    Release: $($release.html_url)" -ForegroundColor Green

        # 上传资产
        $uploadUrl = $release.upload_url -replace '\{\?name,label\}', "?name=$zipName"
        $fileBytes = [System.IO.File]::ReadAllBytes($zipPath)

        $uploadHeaders = @{
            Authorization  = "token $GitHubToken"
            Accept         = "application/vnd.github+json"
            "User-Agent"   = "StickyTodo-publish-script"
            "Content-Type" = "application/zip"
        }

        $asset = Invoke-RestMethod -Method Post `
            -Uri $uploadUrl `
            -Headers $uploadHeaders `
            -Body $fileBytes `

        Write-Host "    资产已上传: $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)" -ForegroundColor Green
        Write-Host "    下载地址: $($asset.browser_download_url)" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "完成。" -ForegroundColor Green
}
finally {
    Pop-Location
}
