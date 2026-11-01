# 下载高精度识别组件（PaddleOCR PP-OCRv6 small + ONNX Runtime）到 tools\ocr\cache，publish.ps1 -Package 打进安装包和绿色版
#   powershell -ExecutionPolicy Bypass -File tools\ocr\fetch.ps1
# 已下载且校验通过的文件不会重复下载。来源：
#   模型：PaddlePaddle 在 Hugging Face 上的官方 ONNX 仓库 PP-OCRv6_small_det_onnx / PP-OCRv6_small_rec_onnx（SHA256 取自仓库的 LFS 记录）
#   字典：没有单独的文件，取自识别模型 inference.yml 的 PostProcess.character_dict
#   onnxruntime.dll：微软 GitHub 发布的 onnxruntime-win-x64（CPU 版），只取其中这一个 dll
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # PowerShell 5.1 显示下载进度会慢很多
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$OrtVersion = '1.31.0'
$cache = Join-Path $PSScriptRoot 'cache'
$hf = 'https://huggingface.co/PaddlePaddle'
$files = @(
    @{ Name = 'det.onnx'; Url = "$hf/PP-OCRv6_small_det_onnx/resolve/main/inference.onnx"; Sha = 'd73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e' },
    @{ Name = 'rec.onnx'; Url = "$hf/PP-OCRv6_small_rec_onnx/resolve/main/inference.onnx"; Sha = '5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634' },
    # inference.yml 不走 LFS，没有官方校验值，这里固定的是 2026-11-01 首次下载时的值，之后内容变了会报错
    @{ Name = 'rec.yml'; Url = "$hf/PP-OCRv6_small_rec_onnx/resolve/main/inference.yml"; Sha = 'ab078671bb49f06228eadccd34f1bb501e157f7a047095ffb943ba81512c77d1' },
    @{ Name = "onnxruntime-win-x64-$OrtVersion.zip"; Url = "https://github.com/microsoft/onnxruntime/releases/download/v$OrtVersion/onnxruntime-win-x64-$OrtVersion.zip"; Sha = '3958d8a44984160692c1b4ffd0de3cb48f8c39f9d34bb31e433db06e30b82195' }
)
# 从 rec.yml 取出的字典（一行一个字，UTF-8 无 BOM，LF 换行）应得到的校验值
$DictSha = 'b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d'

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

# 字典：rec.yml 里 character_dict 下面的列表，每项是普通写法、单引号（'' 表示 '）或双引号（反斜杠转义）
function ConvertFrom-YamlScalar([string]$v) {
    if ($v.Length -ge 2 -and $v[0] -eq "'" -and $v[-1] -eq "'") { return $v.Substring(1, $v.Length - 2).Replace("''", "'") }
    if ($v.Length -ge 2 -and $v[0] -eq '"' -and $v[-1] -eq '"') {
        # 区分大小写（\n 和 \N 不同），PowerShell 的 @{} 不区分，所以用 Dictionary
        $simple = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
        $pairs = @('0', "`0"), @('a', "`a"), @('b', "`b"), @('t', "`t"), @('n', "`n"), @('v', "`v"), @('f', "`f"), @('r', "`r"), @('e', [string][char]27),
            @(' ', ' '), @('"', '"'), @('/', '/'), @('\', '\'), @('N', [string][char]0x85), @('_', [string][char]0xA0), @('L', [string][char]0x2028), @('P', [string][char]0x2029)
        foreach ($p in $pairs) { $simple[$p[0]] = $p[1] }
        return [regex]::Replace($v.Substring(1, $v.Length - 2), '\\(x[0-9A-Fa-f]{2}|u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8}|.)', {
            param($m)
            $e = $m.Groups[1].Value
            if ($e.Length -gt 1) { return [char]::ConvertFromUtf32([Convert]::ToInt32($e.Substring(1), 16)) }
            return $simple[$e]
        })
    }
    return $v
}
$lines = [IO.File]::ReadAllLines((Join-Path $cache 'rec.yml'), (New-Object Text.UTF8Encoding($false)))
$start = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l.Trim() -eq 'character_dict:' })
if ($start -lt 0) { throw 'rec.yml 里没有 character_dict' }
$chars = New-Object Collections.Generic.List[string]
$indent = $null
for ($i = $start + 1; $i -lt $lines.Length; $i++) {
    $m = [regex]::Match($lines[$i], '^(\s*)- (.*)$')
    if (-not $m.Success -or ($indent -ne $null -and $m.Groups[1].Value -ne $indent)) { break }
    $indent = $m.Groups[1].Value
    $chars.Add((ConvertFrom-YamlScalar $m.Groups[2].Value))
}
$dict = Join-Path $cache 'dict.txt'
[IO.File]::WriteAllText($dict, (($chars -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
if ((Get-Sha $dict) -ne $DictSha) { throw "dict.txt 校验失败：$(Get-Sha $dict)，应为 $DictSha" }
"字典 $($chars.Count) 个字"

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

# 上一代（PP-OCRv5）留下的字典已经用不到
Remove-Item (Join-Path $cache 'ppocrv5_dict.txt') -ErrorAction SilentlyContinue

Get-ChildItem $cache -File | Where-Object { $_.Extension -ne '.zip' } | ForEach-Object { '{0,-36} {1,8:N1} MB  {2}' -f $_.Name, ($_.Length / 1MB), (Get-Sha $_.FullName) }
