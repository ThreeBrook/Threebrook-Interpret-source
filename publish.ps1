# 发布 三溪通译（Threebrook Interpret）
#   .\publish.ps1            单文件 exe 到 dist，需要目标电脑已安装 .NET 10 桌面运行时（体积最小）
#   .\publish.ps1 -Portable  自带运行时的单文件 exe 到 dist\portable，任何 Windows 10/11 x64 都能直接运行（体积大）
#   .\publish.ps1 -Package   发布用的文件到 dist\package：
#       ThreebrookInterpret-Setup-<版本>.exe               安装包（框架依赖，约 1 MB）
#       ThreebrookInterpret-Setup-<版本>-Full.exe          完整版安装包（另带高精度识别组件，约 25 MB）
#       ThreebrookInterpret-<版本>-Portable.zip            绿色版（框架依赖，需 .NET 10 桌面运行时）
#       ThreebrookInterpret-<版本>-Portable-Runtime.zip    绿色版（自带运行时）
#       ThreebrookInterpret-<版本>-Portable(-Runtime)-Full.zip  完整版绿色版（两种各一个，另带高精度识别组件）
#       ThreebrookInterpret-OcrPack-<版本>.zip             高精度识别组件（可选，约 24 MB；先运行 tools\ocr\fetch.ps1 下载）
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

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$version = ([xml](Get-Content $project -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$build = Join-Path $PSScriptRoot 'dist\build'
$out = Join-Path $PSScriptRoot 'dist\package'
foreach ($dir in @($build, $out)) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
New-Item -ItemType Directory -Force $out | Out-Null

Publish-App "$build\fd" $false
Publish-App "$build\sc" $true

# 把文件夹里的文件写进 zip，条目路径用 /（PowerShell 5.1 自带的 CreateFromDirectory 会写成 \，有的解压工具不认）
function Add-ZipFiles($archive, [string]$dir, [string]$prefix, [string[]]$exclude = @()) {
    Get-ChildItem $dir -Recurse -File | Where-Object { $exclude -notcontains $_.Name } | ForEach-Object {
        $name = $prefix + $_.FullName.Substring($dir.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}

# 高精度识别组件：PaddleOCR 模型 + onnxruntime.dll（见 Services\OcrService.cs 的 OcrComponent）。
# 完整版安装包把它装到程序旁边的 ocr 文件夹；另外单独打一个组件包，小安装包的用户在设置里"从文件安装"
$ocrCache = Join-Path $PSScriptRoot 'tools\ocr\cache'
$ocrFiles = @('onnxruntime.dll', 'det.onnx', 'rec.onnx', 'ppocrv5_dict.txt')
$ocrStage = $null
if ($ocrFiles | Where-Object { -not (Test-Path (Join-Path $ocrCache $_)) }) {
    Write-Warning '没有高精度识别组件的文件，跳过完整版安装包和组件包；先运行 tools\ocr\fetch.ps1'
}
else {
    $ocrStage = "$build\ocr"
    New-Item -ItemType Directory -Force "$ocrStage\licenses" | Out-Null
    $ocrFiles | ForEach-Object { Copy-Item (Join-Path $ocrCache $_) $ocrStage }
    Copy-Item "$ocrCache\LICENSE-onnxruntime.txt", "$ocrCache\ThirdPartyNotices-onnxruntime.txt" "$ocrStage\licenses"
    # PaddleOCR 模型按 Apache 2.0 发布，许可全文和 Material Symbols 用的是同一份
    Copy-Item "$PSScriptRoot\src\ThreebrookInterpret\Fonts\licenses\LICENSE-MaterialSymbols.txt" "$ocrStage\licenses\LICENSE-PaddleOCR.txt"
    $ortVersion = (Get-Item "$ocrStage\onnxruntime.dll").VersionInfo.ProductVersion
    $componentInfo = @(
        "PP-OCRv5 mobile",
        "三溪通译高精度识别组件 / Threebrook Interpret high-accuracy OCR component",
        "模型 Models: PaddleOCR PP-OCRv5 mobile det + rec (PaddlePaddle Authors, Apache License 2.0; ONNX by RapidAI/RapidOCR v3.10.0)",
        "运行库 Runtime: ONNX Runtime $ortVersion (Microsoft, MIT License)"
    )
    [IO.File]::WriteAllLines("$ocrStage\component.txt", $componentInfo, (New-Object Text.UTF8Encoding($true)))
}

# 安装包：程序 + 附带文件（licenses，完整版再加 ocr 文件夹）的 zip + 尾标
function New-Setup([string]$path, [string]$ocrDir) {
    $exe = [IO.File]::ReadAllBytes("$build\fd\ThreebrookInterpret.exe")
    $zipStream = New-Object IO.MemoryStream
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    Add-ZipFiles $archive "$build\fd" '' @('ThreebrookInterpret.exe')
    if ($ocrDir) { Add-ZipFiles $archive $ocrDir 'ocr/' }
    $archive.Dispose()
    $zip = $zipStream.ToArray()
    $trailer = New-Object byte[] 32
    [BitConverter]::GetBytes([long]$exe.Length).CopyTo($trailer, 0)
    [BitConverter]::GetBytes([long]$zip.Length).CopyTo($trailer, 8)
    [Text.Encoding]::ASCII.GetBytes('TBINTP01').CopyTo($trailer, 24)
    $setup = [IO.File]::Create($path)
    $setup.Write($exe, 0, $exe.Length); $setup.Write($zip, 0, $zip.Length); $setup.Write($trailer, 0, 32)
    $setup.Dispose()
}
New-Setup "$out\ThreebrookInterpret-Setup-$version.exe" $null
if ($ocrStage) { New-Setup "$out\ThreebrookInterpret-Setup-$version-Full.exe" $ocrStage }

# 绿色版：程序旁边放 portable 文件，数据存在旁边的 data 文件夹
$note = "有这个文件时，三溪通译以绿色版运行：设置、术语表和剪贴板历史都保存在旁边的 data 文件夹，不写 %AppData%。删掉这个文件就按安装版的位置保存。`r`n"
# 完整版绿色版在程序旁边多一个 ocr 文件夹（高精度识别组件），解压就能用
$kinds = @(@{ Src = 'fd'; Name = 'Portable' }, @{ Src = 'sc'; Name = 'Portable-Runtime' })
foreach ($kind in $kinds) {
    $stage = "$build\portable-$($kind.Src)\ThreebrookInterpret"
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item "$build\$($kind.Src)\*" $stage -Recurse
    [IO.File]::WriteAllText("$stage\portable", $note, (New-Object Text.UTF8Encoding($true)))
    $suffixes = @('')
    if ($ocrStage) { $suffixes += '-Full' }
    foreach ($suffix in $suffixes) {
        $file = [IO.File]::Create("$out\ThreebrookInterpret-$version-$($kind.Name)$suffix.zip")
        $archive = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
        Add-ZipFiles $archive $stage 'ThreebrookInterpret/'
        if ($suffix) { Add-ZipFiles $archive $ocrStage 'ThreebrookInterpret/ocr/' }
        $archive.Dispose(); $file.Dispose()
    }
}

# 单独的组件包（给小安装包、绿色版的用户）
if ($ocrStage) {
    $file = [IO.File]::Create("$out\ThreebrookInterpret-OcrPack-$version.zip")
    $archive = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
    Add-ZipFiles $archive $ocrStage ''
    $archive.Dispose(); $file.Dispose()
}

# 打包记录：哪次提交、许可协议和各文件的 SHA-256，对得上就说明安装包里的协议就是仓库里这一版
$info = @("Version: $version", "Commit:  $commit", "Built:   $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))", '')
foreach ($file in @(Get-ChildItem "$PSScriptRoot\src\ThreebrookInterpret\Setup\License*.txt") + @(Get-ChildItem $out -File)) {
    $info += '{0}  {1}' -f (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower(), $file.Name
}
[IO.File]::WriteAllLines("$out\build-info.txt", $info)

Show-Files $out
