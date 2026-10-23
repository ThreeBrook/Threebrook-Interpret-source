# ThreeBrook Trans

轻巧的 Windows 托盘翻译工具：划词翻译、截图翻译、输入翻译、截图钉图、剪贴板历史。程序本体约 300 KB。

## 运行

```bash
dotnet build -c Release
```

运行 `src\ThreeBrookTrans\bin\Release\net8.0-windows\ThreeBrookTrans.exe`，或用发布脚本生成单文件：

```bash
powershell -ExecutionPolicy Bypass -File publish.ps1
```

- `dist\ThreeBrookTrans.exe`：约 300 KB，需要已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)
- `publish.ps1 -Portable`：自带运行时，放到任何 Windows 10/11 x64 电脑都能直接运行，体积较大

程序已在运行时再次打开 exe，会弹出设置窗口。

## 快捷键（可在设置中修改）

| 快捷键 | 功能 |
|---|---|
| Alt+Q | 输入翻译 |
| Alt+D | 划词翻译（先选中文字） |
| Alt+S | 截图：框选后可识别文字、翻译、钉图、复制、保存 |
| Alt+W | 截图翻译：框选后直接识别并翻译 |
| Alt+H | 剪贴板历史（文字 + 图片），Enter 粘贴 |

截图界面：Enter / Ctrl+C 复制，Ctrl+S 保存，F3 钉图，O 识别文字，T 翻译，右键 / Esc 取消，选区可拖动。
钉图窗口：拖动移动，滚轮缩放，Ctrl+滚轮调透明度，双击 / Esc 关闭，右键更多操作。

## 翻译引擎

| 引擎 | 需要 | 说明 |
|---|---|---|
| 必应翻译 | 无 | 网页版接口，非官方，默认启用 |
| Yandex | 无 | 安卓客户端接口，非官方，默认启用 |
| OpenAI 兼容接口 | 地址 + 模型名 | 本地 Ollama / LM Studio / vLLM，或 DeepSeek、智谱、硅基流动等在线大模型 |
| 百度、腾讯、有道、Azure、DeepL | 各自的 Key | 官方 API，稳定，有免费额度 |

非官方接口可能随时失效（2026-10 时微软 Edge 翻译接口已下线），所以支持同时启用多个引擎、结果并排显示。

**接本地大模型**：设置 → 翻译引擎 → 添加 → OpenAI 兼容接口，填写接口地址（Ollama 为 `http://localhost:11434/v1`）和模型名，点"测试翻译"。Qwen3 等推理模型建议保持"关闭思考模式"。

## 文字识别（OCR）

使用 Windows 自带 OCR，离线、不增加体积。默认跟随系统语言（中文系统可识别中英文）。
识别日语、韩语、泰语等：Windows 设置 → 时间和语言 → 语言 → 添加该语言（勾选"光学字符识别"），然后在设置 → 截图与识别 中选择。

## 数据位置

`%AppData%\ThreeBrookTrans\`

- `settings.json`：配置
- `history\`：剪贴板历史（只存本机；密码管理器等标记为"不记录"的内容会跳过）
- `error.log`：错误日志

截图默认保存到 `图片\ThreeBrookTrans`。

## 代码结构

```
src/ThreeBrookTrans/
  App.xaml.cs            托盘、快捷键、各功能入口
  Translation/           翻译引擎（实现 ITranslator，在 TranslatorFactory 注册）
  Services/              快捷键、划词、截屏、OCR、剪贴板历史、配置、开机自启
  Views/                 翻译窗口、截图遮罩、钉图、剪贴板历史、设置
```
