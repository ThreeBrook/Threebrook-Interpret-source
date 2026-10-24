# 发布 三溪通译（Threebrook Interpret）
#   .\publish.ps1            单文件 exe 到 dist，需要目标电脑已安装 .NET 10 桌面运行时（体积最小）
#   .\publish.ps1 -Portable  自带运行时的单文件 exe 到 dist\portable，任何 Windows 10/11 x64 都能直接运行（体积大）
#   .\publish.ps1 -Package   发布用的三个文件到 dist\package：
#       ThreebrookInterpret-Setup-<版本>.exe               安装包（框架依赖，约 1 MB）
#       ThreebrookInterpret-<版本>-Portable.zip            绿色版（框架依赖，需 .NET 10 桌面运行时）
#       ThreebrookInterpret-<版本>-Portable-Runtime.zip    绿色版（自带运行时）
# 安装包 = 程序 exe + 附带文件 zip + 32 字节尾标（见 src\ThreebrookInterpret\Setup\SetupPayload.cs）
param([switch]$Portable, [switch]$Package)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ThreebrookInterpret\ThreebrookInterpret.csproj'

function Publish-App([string]$output, [bool]$selfContained) {
    $extra = if ($selfContained) { @('-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') } else { @() }
    dotnet publish $project -c Release -r win-x64 --self-contained $(if ($selfContained) { 'true' } else { 'false' }) `
        -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false @extra -o $output
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

# 安装包：程序 + 附带文件（licenses）的 zip + 尾标
$exe = [IO.File]::ReadAllBytes("$build\fd\ThreebrookInterpret.exe")
$zipStream = New-Object IO.MemoryStream
$archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
Add-ZipFiles $archive "$build\fd" '' @('ThreebrookInterpret.exe')
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
foreach ($kind in @(@{ Src = 'fd'; Zip = "ThreebrookInterpret-$version-Portable.zip" }, @{ Src = 'sc'; Zip = "ThreebrookInterpret-$version-Portable-Runtime.zip" })) {
    $stage = "$build\portable-$($kind.Src)\ThreebrookInterpret"
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item "$build\$($kind.Src)\*" $stage -Recurse
    [IO.File]::WriteAllText("$stage\portable", $note, (New-Object Text.UTF8Encoding($true)))
    $file = [IO.File]::Create("$out\$($kind.Zip)")
    $archive = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create)
    Add-ZipFiles $archive $stage 'ThreebrookInterpret/'
    $archive.Dispose(); $file.Dispose()
}

Show-Files $out
