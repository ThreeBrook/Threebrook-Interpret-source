# 三溪通译 Threebrook Interpret

轻巧的 Windows 托盘翻译工具：划词翻译、截图翻译、输入翻译、截图标注与钉图、剪贴板历史。程序本体约 750 KB（含内嵌字体和图标）。

## 运行

```bash
dotnet build -c Release
```

运行 `src\ThreebrookInterpret\bin\Release\net10.0-windows\ThreebrookInterpret.exe`，或用发布脚本生成单文件：

```bash
powershell -ExecutionPolicy Bypass -File publish.ps1
```

- `dist\ThreebrookInterpret.exe`：约 750 KB，需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
- `publish.ps1 -Portable`：自带运行时，放到任何 Windows 10/11 x64 电脑都能直接运行，体积较大

程序已在运行时再次打开 exe，会弹出设置窗口。

## 安装包与绿色版

```bash
powershell -ExecutionPolicy Bypass -File publish.ps1 -Package
```

在 `dist\package` 生成三个文件（均需 Windows 10/11 x64）：

| 文件 | 说明 |
|---|---|
| `ThreebrookInterpret-Setup-<版本>.exe` | 安装包，约 0.9 MB，需要 .NET 10 桌面运行时 |
| `ThreebrookInterpret-<版本>-Portable.zip` | 绿色版，约 0.4 MB，需要 .NET 10 桌面运行时 |
| `ThreebrookInterpret-<版本>-Portable-Runtime.zip` | 绿色版，自带运行时，约 67 MB，解压就能用 |

安装包是自制的（参考三溪寻影的安装程序）：安装包就是程序本身，末尾附带许可文件和一段尾标，运行时进入安装向导，界面与软件一致。

- 只给当前用户安装，不需要管理员权限，不联网；默认装到 `%LocalAppData%\Programs\ThreebrookInterpret`，建开始菜单快捷方式，可选桌面快捷方式和开机自启
- 覆盖安装：先通知正在运行的三溪通译退出；新文件全部写好才替换，出错或中止会撤回，原来的版本照常可用；设置和历史不动
- 卸载：「设置 › 应用」里卸载，或运行 `ThreebrookInterpret.exe --uninstall`；默认保留设置和历史，勾选后才删除
- 静默：`Setup.exe --silent [--dir 目录] [--desktop] [--autostart]`，`ThreebrookInterpret.exe --uninstall --silent [--delete-data]`；结果写到 `%TEMP%\ThreebrookInterpret-Setup.log`
- 测试：加 `--test` 换用另一个 AppId 和名字，不碰开机自启和真实数据；环境变量 `THREEBROOK_SETUP_FAIL_AT=<n>|end` 模拟出错，验证回滚

绿色版的 exe 旁边有一个 `portable` 文件，程序看到它就把所有数据存到旁边的 `data` 文件夹，不写 `%AppData%`，整个文件夹拷走就能接着用。

## 快捷键（可在设置中修改）

| 快捷键 | 功能 |
|---|---|
| Alt+Q | 输入翻译 |
| Alt+D | 划词翻译（先选中文字） |
| Alt+S | 截图：框选后可识别文字、翻译、钉图、复制、保存 |
| Alt+W | 截图翻译：框选后直接识别并翻译 |
| Alt+H | 剪贴板历史（文字 + 图片），Enter 粘贴 |

截图时鼠标移到哪个窗口就高亮它，单击直接选中该窗口（桌面上单击选中整个屏幕），拖动则自由框选。框选后拖动边和角调整大小，方向键微调位置、Shift+方向键调整大小。

截图界面：Enter / Ctrl+C 复制，Ctrl+S 保存，F3 钉图，O 识别文字，T 翻译，右键 / Esc 取消，选区可拖动。

### 截图标注

框选后工具栏左侧是标注工具（数字键 1–8）：矩形、椭圆、箭头、画笔、荧光笔、文字、马赛克、序号。选中工具后下方出现粗细和颜色。

- 矩形、椭圆按住 Shift 画正方形、圆；文字点击放置，Esc 或 Ctrl+Enter 完成输入
- Ctrl+Z 撤销、Ctrl+Y 重做；再点一次当前工具或右键退出工具
- 复制、保存、钉图都会带上标注；识别文字仍用原图，不受标注影响
- 画了标注后选区锁定，不会误拖丢失；右键（未选工具时）清除选区和标注

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

## 术语表

设置 → 术语表：填写成对的词（如 三溪 ⇄ Threebrook、周报 ⇄ weekly digest），双向生效，原文里出现哪一边就固定翻成另一边。

- 大模型：把用到的术语写进提示词
- 必应、Yandex 等机器翻译：翻译前把术语换成占位符，翻完换回固定译法（实测两者都会原样保留占位符）
- 英文术语按整词匹配，不区分大小写；长的术语优先匹配

## 文字识别（OCR）

使用 Windows 自带 OCR，离线、不增加体积。默认跟随系统语言（中文系统可识别中英文）。
识别日语、韩语、泰语等：Windows 设置 → 时间和语言 → 语言 → 添加该语言（勾选"光学字符识别"），然后在设置 → 截图与识别 中选择。

## 界面语言

简体中文 / English，在 设置 → 常规 → 界面语言 切换，立即生效；默认跟随 Windows 显示语言（中文系统用中文，其余用英文）。安装程序沿用软件里选的语言。

界面文字在 `src/ThreebrookInterpret/Localization/Strings.zh.xaml` 和 `Strings.en.xaml`，两个文件的键一一对应：XAML 用 `{DynamicResource S.键}`，代码用 `Loc.T("键")` / `Loc.F("键", 参数)`。新增界面文字时两个文件都要加。

## 外观

按[三溪外观规范](doc/01-ui/三溪外观规范.md)：溪青强调色、米白 / 墨绿浅深两套主题（默认跟随系统，设置 → 常规 → 外观 可固定），标题用站酷小薇 / Bricolage Grotesque，等宽用 JetBrains Mono，图标用 Material Symbols 线框款。产品色暮紫 `#7460C4`。

## 数据位置

`%AppData%\ThreebrookInterpret\`（绿色版在程序旁边的 `data\`）

- `settings.json`：配置（API Key、密钥用 Windows DPAPI 加密保存，只有本机同一账户能解开；换电脑或账户后需要重新填写）
- `history\`：剪贴板历史（只存本机；密码管理器等标记为"不记录"的内容会跳过）
- `error.log`：错误日志

截图默认保存到 `图片\三溪通译`。

从旧版（ThreeBrookTrans.exe）升级：新版第一次启动时会把 `%AppData%\ThreeBrookTrans` 整体迁移到 `%AppData%\ThreebrookInterpret`，开着的开机自启也会改为指向新程序；旧版保存在 `图片\ThreeBrookTrans` 的截图保持原处。

## 开发工具

| 命令 | 作用 |
|---|---|
| `python tools/fonts/build_fonts.py <下载目录>` | 裁剪界面字体与图标字体（新增标题字、图标后运行，说明见脚本开头） |
| `dotnet run --project tools/IconGen` | 生成产品图标 `Assets/AppIcon.ico` 等，预览图 `tools/IconGen/preview.png` |
| `ThreebrookInterpret.exe --render-previews <目录>` | 把各窗口按浅色 / 深色渲染成 PNG（屏幕外渲染，不需要桌面、不读写用户数据） |

## 代码结构

```
src/ThreebrookInterpret/
  App.xaml.cs            托盘、快捷键、各功能入口
  Translation/           翻译引擎（实现 ITranslator，在 TranslatorFactory 注册）
  Services/              快捷键、划词、截屏、OCR、剪贴板历史、配置、开机自启
  Views/                 翻译窗口、截图遮罩与标注、钉图、剪贴板历史、设置、自绘标题栏
  Setup/                 安装 / 卸载向导与引擎（安装包格式、回滚、快捷方式、卸载信息）
  Theme/                 浅色 / 深色颜色（由 doc/01-ui 的设计变量生成）、字体与图标、控件样式
  Fonts/ Assets/         裁剪后的字体（含许可）、产品图标
```
