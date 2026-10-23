# 发布 三溪通译（Threebrook Interpret）到 dist 目录
#   .\publish.ps1            单文件 exe，需要目标电脑已安装 .NET 10 桌面运行时（体积最小）
#   .\publish.ps1 -Portable  自带运行时，任何 Windows 10/11 x64 都能直接运行（体积大）
param([switch]$Portable)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ThreeBrookTrans\ThreeBrookTrans.csproj'
$output = Join-Path $PSScriptRoot ($(if ($Portable) { 'dist\portable' } else { 'dist' }))

$selfContained = if ($Portable) { 'true' } else { 'false' }
$extra = if ($Portable) { @('-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') } else { @() }

dotnet publish $project -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false @extra -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem $output -File | ForEach-Object { '{0,-28} {1,10:N0} KB' -f $_.Name, ($_.Length / 1KB) }
