# 发布 三溪通译（Threebrook Interpret）
#   .\publish.ps1            单文件 exe 到 dist，需要目标电脑已安装 .NET 10 桌面运行时（开发自用，体积最小）
#   .\publish.ps1 -Portable  自带运行时的单文件 exe 到 dist\portable（开发自用）
#   .\publish.ps1 -Package   发布用的两个文件到 dist\package，都自带运行时和高精度识别组件，下载就能用：
#       ThreebrookInterpret-Setup-<版本>.exe       安装包
#       ThreebrookInterpret-<版本>-Portable.zip    绿色版（解压即用，数据存在旁边的 data 文件夹）
#     高精度识别组件（PaddleOCR 模型 + onnxruntime.dll）先运行 tools\ocr\fetch.ps1 下载到 tools\ocr\cache
# 安装包 = 程序 exe + 附带文件 zip + 32 字节尾标（见 src\ThreebrookInterpret\Setup\SetupPayload.cs）
# -Package 要求 src 和本脚本没有未提交的改动，免得发出去的安装包（含许可协议）和仓库对不上；临时试包加 -AllowDirty
param([switch]$Portable, [switch]$Package, [switch]$AllowDirty)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ThreebrookInterpret\ThreebrookInterpret.csproj'

# 当前提交写进程序的 InformationalVersion（版本号+提交），看 exe 属性就知道是哪次提交打的
$commit = (git -C $PSScriptRoot rev-parse --short HEAD)
$dirty = git -C $PSScriptRoot status --porcelain -- src publish.ps1
if ($dirty) { $commit += '-dirty' }
if ($Package -and $dirty -and -not $AllowDirty) {
    throw "src 或 publish.ps1 有未提交的改动，先提交再打包（临时试包加 -AllowDirty）：`n$($dirty -join "`n")"
}

function Publish-App([string]$output, [bool]$selfContained) {
    $extra = if ($selfContained) { @('-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') } else { @() }
    dotnet publish $project -c Release -r win-x64 --self-contained $(if ($selfContained) { 'true' } else { 'false' }) `
        -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false -p:SourceRevisionId=$commit @extra -o $output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Show-Files([string]$dir) {
    Get-ChildItem $dir -File | ForEach-Object { '{0,-50} {1,10:N0} KB' -f $_.Name, ($_.Length / 1KB) }
}

if (-not $Package) {
    $output = Join-Path $PSScriptRoot ($(if ($Portable) { 'dist\portable' } else { 'dist' }))
    Publish-App $output $Portable.IsPresent
    Show-Files $output
    exit 0
}

# 高精度识别组件要先下载好：发布的两个版本都带它
$ocrCache = Join-Path $PSScriptRoot 'tools\ocr\cache'
$ocrFiles = @('onnxruntime.dll', 'det.onnx', 'rec.onnx', 'ppocrv5_dict.txt', 'LICENSE-onnxruntime.txt', 'ThirdPartyNotices-onnxruntime.txt')
$missing = $ocrFiles | Where-Object { -not (Test-Path (Join-Path $ocrCache $_)) }
if ($missing) { throw "缺少高精度识别组件的文件（$($missing -join '、')），先运行 tools\ocr\fetch.ps1" }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$version = ([xml](Get-Content $project -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$build = Join-Path $PSScriptRoot 'dist\build'
$out = Join-Path $PSScriptRoot 'dist\package'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
# dist\package 只删上次打包生成的文件；别的（比如解压在这里试用的绿色版，旁边有它的 data 数据）一律不动
Get-ChildItem $out -File | Where-Object { $_.Name -like 'ThreebrookInterpret-*' -or $_.Name -eq 'build-info.txt' } | Remove-Item -Force

# 程序：自带运行时的单文件 exe 和附带文件（licenses），再加 ocr 文件夹
$app = "$build\app"
Publish-App $app $true
$ocr = "$app\ocr"
New-Item -ItemType Directory -Force "$ocr\licenses" | Out-Null
$ocrFiles[0..3] | ForEach-Object { Copy-Item (Join-Path $ocrCache $_) $ocr }
Copy-Item "$ocrCache\LICENSE-onnxruntime.txt", "$ocrCache\ThirdPartyNotices-onnxruntime.txt" "$ocr\licenses"
# PaddleOCR 模型按 Apache 2.0 发布，许可全文和 Material Symbols 用的是同一份
Copy-Item "$PSScriptRoot\src\ThreebrookInterpret\Fonts\licenses\LICENSE-MaterialSymbols.txt" "$ocr\licenses\LICENSE-PaddleOCR.txt"
$ortVersion = (Get-Item "$ocr\onnxruntime.dll").VersionInfo.ProductVersion
[IO.File]::WriteAllLines("$ocr\component.txt", @(
    "PP-OCRv5 mobile",
    "三溪通译高精度识别组件 / Threebrook Interpret high-accuracy OCR component",
    "模型 Models: PaddleOCR PP-OCRv5 mobile det + rec (PaddlePaddle Authors, Apache License 2.0; ONNX by RapidAI/RapidOCR v3.10.0)",
    "运行库 Runtime: ONNX Runtime $ortVersion (Microsoft, MIT License)"
), (New-Object Text.UTF8Encoding($true)))

# 把文件夹里的文件写进 zip，条目路径用 /（PowerShell 5.1 自带的 CreateFromDirectory 会写成 \，有的解压工具不认）
function Add-ZipFiles($archive, [string]$dir, [string]$prefix, [string[]]$exclude = @()) {
    Get-ChildItem $dir -Recurse -File | Where-Object { $exclude -notcontains $_.Name } | ForEach-Object {
        $name = $prefix + $_.FullName.Substring($dir.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}

# 安装包：程序 + 附带文件（licenses、ocr）的 zip + 尾标
$exe = [IO.File]::ReadAllBytes("$app\ThreebrookInterpret.exe")
$zipStream = New-Object IO.MemoryStream
$archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
Add-ZipFiles $archive $app '' @('ThreebrookInterpret.exe')
$archive.Dispose()
$zip = $zipStream.ToArray()
$trailer = New-Object byte[] 32
[BitConverter]::GetBytes([long]$exe.Length).CopyTo($trailer, 0)
[BitConverter]::GetBytes([long]$zip.Length).CopyTo($trailer, 8)
[Text.Encoding]::ASCII.GetBytes('TBINTP01').CopyTo($trailer, 24)
$setup = [IO.File]::Create("$out\ThreebrookInterpret-Setup-$version.exe")
$setup.Write($exe, 0, $exe.Length); $setup.Write($zip, 0, $zip.Length); $setup.Write($trailer, 0, 32)
$setup.Dispose()

# 绿色版：程序旁边放 portable 文件，数据存在旁边的 data 文件夹
$note = "有这个文件时，三溪通译以绿色版运行：设置、术语表和剪贴板历史都保存在旁边的 data 文件夹，不写 %AppData%。删掉这个文件就按安装版的位置保存。`r`n"
[IO.File]::WriteAllText("$build\portable", $note, (New-Object Text.UTF8Encoding($true)))
$file = [IO.File]::Create("$out\ThreebrookInterpret-$version-Portable.zip")
$archive = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
Add-ZipFiles $archive $app 'ThreebrookInterpret/'
[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, "$build\portable", 'ThreebrookInterpret/portable') | Out-Null
$archive.Dispose(); $file.Dispose()

# 打包记录：哪次提交、许可协议和各文件的 SHA-256，对得上就说明安装包里的协议就是仓库里这一版
$info = @("Version: $version", "Commit:  $commit", "Built:   $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))", '')
foreach ($file in @(Get-ChildItem "$PSScriptRoot\src\ThreebrookInterpret\Setup\License*.txt") + @(Get-ChildItem $out -File)) {
    $info += '{0}  {1}' -f (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower(), $file.Name
}
[IO.File]::WriteAllLines("$out\build-info.txt", $info)

Show-Files $out
