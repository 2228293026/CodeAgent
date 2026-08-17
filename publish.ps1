<#
.SYNOPSIS
    发布 CodeAgent 为可直接分发的可执行程序。

.DESCRIPTION
    默认以「自包含 + 单文件」方式发布（--self-contained true -p:PublishSingleFile=true），
    对方机器无需安装 .NET 运行时即可运行。同时把示例配置 codeagent.example.json
    复制到输出目录，方便接收者参考生成自己的 codeagent.json。

.PARAMETER RuntimeIdentifier
    目标运行时标识。默认 win-x64。常见值：win-x64 / linux-x64 / osx-x64 / osx-arm64。
    可多次指定（如 "win-x64","linux-x64","osx-x64"）以一次产出多平台。

.PARAMETER Configuration
    构建配置。默认 Release。

.PARAMETER OutputDir
    输出根目录。默认仓库根下的 dist/，每个平台再分一个子目录。

.PARAMETER SelfContained
    是否打包运行时。默认 $true（对方免装 .NET）。设 $false 则对方需自行安装 .NET 10 运行时。

.PARAMETER SingleFile
    是否打包为单文件 exe。默认 $true。

.EXAMPLE
    .\publish.ps1
    发布 win-x64 自包含单文件到 dist/win-x64/。

.EXAMPLE
    .\publish.ps1 -RuntimeIdentifier win-x64,linux-x64,osx-x64
    一次发布三个平台到 dist/<rid>/。

.EXAMPLE
    .\publish.ps1 -SelfContained $false
    发布依赖框架的版本（体积小，但对方需装 .NET 10 运行时）。
#>
[CmdletBinding()]
param(
    [string[]] $RuntimeIdentifier = @('win-x64'),
    [string]   $Configuration = 'Release',
    [string]   $OutputDir = (Join-Path $PSScriptRoot 'dist'),
    [bool]     $SelfContained = $true,
    [bool]     $SingleFile = $true
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src' 'CodeAgent' 'CodeAgent.csproj'
if (-not (Test-Path $project)) {
    Write-Error "找不到项目文件: $project"
    exit 1
}

$exampleConfig = Join-Path $PSScriptRoot 'codeagent.example.json'

$scArg = if ($SelfContained) { 'true' } else { 'false' }
$sfArg = if ($SingleFile)    { 'true' } else { 'false' }

foreach ($rid in $RuntimeIdentifier) {
    $out = Join-Path $OutputDir $rid
    Write-Host "`n=== 发布 $rid ($Configuration, self-contained=$scArg, single-file=$sfArg) ===" -ForegroundColor Cyan
    Write-Host "输出目录: $out"

    dotnet publish "$project" `
        -c $Configuration `
        -r $rid `
        --self-contained $scArg `
        -p:PublishSingleFile=$sfArg `
        -p:PublishTrimmed=false `
        -o "$out"

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish 失败 (rid=$rid)"
        exit $LASTEXITCODE
    }

    # 附带示例配置，方便接收者生成自己的 codeagent.json
    if (Test-Path $exampleConfig) {
        Copy-Item -Force $exampleConfig (Join-Path $out 'codeagent.example.json')
        Write-Host "已复制示例配置 -> $out/codeagent.example.json"
    }

    Write-Host "完成: $out" -ForegroundColor Green
}

Write-Host "`n全部发布完成。分发时把对应平台的 dist/<rid>/ 目录发给对方即可。" -ForegroundColor Green
Write-Host "对方使用步骤:" -ForegroundColor Yellow
Write-Host "  1. 进入 dist/<rid>/"
Write-Host "  2. 复制 codeagent.example.json 为 codeagent.json，按说明填写 provider/model"
Write-Host "  3. 设置 API Key 环境变量（如 `$env:OPENAI_API_KEY='sk-xxx'）"
if (-not $SelfContained) {
    Write-Host "  注意: 此版本依赖框架，对方需先安装 .NET 10 运行时。" -ForegroundColor Yellow
}
