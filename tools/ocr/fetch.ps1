# 下载高精度识别组件（PaddleOCR PP-OCRv5 mobile + ONNX Runtime）到 tools\ocr\cache，publish.ps1 -Package 打成单独的组件 zip
#   powershell -ExecutionPolicy Bypass -File tools\ocr\fetch.ps1
# 已下载且校验通过的文件不会重复下载。来源：
#   模型、字典：RapidAI 在 ModelScope 上的 RapidOCR 官方仓库（v3.10.0，SHA256 取自 RapidOCR 的 default_models.yaml）
#   onnxruntime.dll：微软 GitHub 发布的 onnxruntime-win-x64（CPU 版），只取其中这一个 dll
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # PowerShell 5.1 显示下载进度会慢很多
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$OrtVersion = '1.31.0'
$cache = Join-Path $PSScriptRoot 'cache'
$modelBase = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0'
$files = @(
    @{ Name = 'det.onnx'; Url = "$modelBase/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx"; Sha = '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae' },
    @{ Name = 'rec.onnx'; Url = "$modelBase/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx"; Sha = '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5' },
    # 字典没有官方校验值，这里固定的是 2026-10-25 首次下载时的值，之后内容变了会报错
    @{ Name = 'ppocrv5_dict.txt'; Url = "$modelBase/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt"; Sha = 'd1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b' },
    @{ Name = "onnxruntime-win-x64-$OrtVersion.zip"; Url = "https://github.com/microsoft/onnxruntime/releases/download/v$OrtVersion/onnxruntime-win-x64-$OrtVersion.zip"; Sha = '3958d8a44984160692c1b4ffd0de3cb48f8c39f9d34bb31e433db06e30b82195' }
)

New-Item -ItemType Directory -Force $cache | Out-Null
function Get-Sha([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLower() }

foreach ($file in $files) {
    $path = Join-Path $cache $file.Name
    if ((Test-Path $path) -and (-not $file.Sha -or (Get-Sha $path) -eq $file.Sha)) {
        "已有 $($file.Name)"
        continue
    }
    "下载 $($file.Url)"
    Invoke-WebRequest -UseBasicParsing -Uri $file.Url -OutFile "$path.part"
    $sha = Get-Sha "$path.part"
    if ($file.Sha -and $sha -ne $file.Sha) {
        Remove-Item "$path.part"
        throw "$($file.Name) 校验失败：$sha，应为 $($file.Sha)"
    }
    Move-Item "$path.part" $path -Force
    '  {0:N1} MB  sha256 {1}' -f ((Get-Item $path).Length / 1MB), $sha
}

# 从 ONNX Runtime 发布包里只取 onnxruntime.dll（和它的许可）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $cache "onnxruntime-win-x64-$OrtVersion.zip"))
try {
    foreach ($entry in $zip.Entries) {
        $target = switch -Regex ($entry.FullName) {
            '/lib/onnxruntime\.dll$' { 'onnxruntime.dll' }
            '/LICENSE$' { 'LICENSE-onnxruntime.txt' }
            '/ThirdPartyNotices\.txt$' { 'ThirdPartyNotices-onnxruntime.txt' }
            default { $null }
        }
        if ($target) { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $cache $target), $true) }
    }
}
finally { $zip.Dispose() }

Get-ChildItem $cache -File | Where-Object { $_.Extension -ne '.zip' } | ForEach-Object { '{0,-36} {1,8:N1} MB  {2}' -f $_.Name, ($_.Length / 1MB), (Get-Sha $_.FullName) }
