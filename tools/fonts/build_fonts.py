"""
裁剪并内嵌界面字体（三溪外观规范：标题 站酷小薇 / Bricolage Grotesque，等宽 JetBrains Mono，图标 Material Symbols 线框款）。

只保留界面实际用到的字形，几款字体合计约 100 KB。界面新增标题文字或图标后重新运行：

    python build_fonts.py <下载目录>

下载目录里需要（均来自 Google 官方仓库，OFL 1.1 / Apache 2.0）：
    ZCOOLXiaoWei-Regular.ttf            github.com/google/fonts  ofl/zcoolxiaowei/
    BricolageGrotesque-VF.ttf           github.com/google/fonts  ofl/bricolagegrotesque/BricolageGrotesque[opsz,wdth,wght].ttf
    JetBrainsMono-VF.ttf                github.com/google/fonts  ofl/jetbrainsmono/JetBrainsMono[wght].ttf
    MaterialSymbolsOutlined-VF.ttf      github.com/google/material-design-icons  variablefont/MaterialSymbolsOutlined[FILL,GRAD,opsz,wght].ttf
    MaterialSymbolsOutlined.codepoints  同上目录的 .codepoints 文件

依赖：fonttools（pip install fonttools）
"""
import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "src", "ThreebrookInterpret", "Fonts"))

ASCII = "".join(chr(c) for c in range(0x20, 0x7F))

# 等宽字体用于快捷键、耗时、尺寸、路径
MONO_EXTRA = "→←↑↓·×…⇄–—"

# 界面用到的 Material Symbols（名称见 .codepoints 文件），新增图标时加在这里，并在 Theme/Icons.xaml 登记
ICONS = [
    "close", "remove", "push_pin", "keep", "content_copy", "save", "translate", "content_paste",
    "history", "settings", "search", "delete", "screenshot_region", "document_scanner", "keyboard",
    "tune", "info", "image", "notes", "check", "add", "arrow_upward", "arrow_downward", "folder_open",
    "refresh", "expand_more", "error", "check_circle", "zoom_in", "opacity", "logout", "dark_mode",
    "light_mode", "contrast", "open_in_new", "auto_awesome", "keyboard_return",
    # 截图标注工具
    "rectangle", "circle", "arrow_outward", "draw", "ink_highlighter", "title", "blur_on", "counter_1", "undo", "redo",
    # 术语表
    "menu_book", "swap_horiz",
    # 长截图
    "swipe_vertical", "pause", "play_arrow",
]


def display_chars():
    """标题字体只用于标题，字表在 display-chars.txt 里维护。"""
    with open(os.path.join(HERE, "display-chars.txt"), encoding="utf-8") as f:
        text = "".join(line.split("#", 1)[0] for line in f)
    return "".join(sorted(set(text) - set(" \t\r\n"))) + ASCII


def rename(font, family):
    """裁剪 / 固定字重后统一家族名，WPF 用 "#家族名" 引用。"""
    for record in font["name"].names:
        if record.nameID in (1, 16):
            record.string = family
        elif record.nameID in (2, 17):
            record.string = "Regular"
        elif record.nameID in (4,):
            record.string = family
        elif record.nameID == 6:
            record.string = family.replace(" ", "") + "-Regular"
    # 只保留 Windows 能用的 name 记录，避免家族名不一致
    font["name"].names = [r for r in font["name"].names if r.nameID not in (16, 17, 21, 22, 25)]


def save_subset(font, text=None, unicodes=None, path=None, family=None):
    options = subset.Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.notdef_outline = True
    options.hinting = True
    subsetter = subset.Subsetter(options)
    subsetter.populate(text=text or "", unicodes=unicodes or [])
    subsetter.subset(font)
    if family:
        rename(font, family)
    font.save(path)
    print(f"{os.path.basename(path):34} {os.path.getsize(path) / 1024:7.1f} KB")


def main(src):
    os.makedirs(OUT, exist_ok=True)

    zh = TTFont(os.path.join(src, "ZCOOLXiaoWei-Regular.ttf"))
    save_subset(zh, text=display_chars(), path=os.path.join(OUT, "ZCOOLXiaoWei-Subset.ttf"), family="ZCOOL XiaoWei")

    # 规范：Bricolage Grotesque wght 700、opsz 32
    en = instancer.instantiateVariableFont(TTFont(os.path.join(src, "BricolageGrotesque-VF.ttf")),
                                           {"wght": 700, "opsz": 32, "wdth": 100})
    save_subset(en, text=ASCII, path=os.path.join(OUT, "BricolageGrotesque-Bold-Subset.ttf"), family="Bricolage Grotesque")

    mono = instancer.instantiateVariableFont(TTFont(os.path.join(src, "JetBrainsMono-VF.ttf")), {"wght": 400})
    save_subset(mono, text=ASCII + MONO_EXTRA, path=os.path.join(OUT, "JetBrainsMono-Subset.ttf"), family="JetBrains Mono")

    codepoints = {}
    with open(os.path.join(src, "MaterialSymbolsOutlined.codepoints"), encoding="utf-8") as f:
        for line in f:
            name, code = line.split()
            codepoints[name] = int(code, 16)
    missing = [n for n in ICONS if n not in codepoints]
    if missing:
        sys.exit("Material Symbols 中找不到: " + ", ".join(missing))

    icons = instancer.instantiateVariableFont(TTFont(os.path.join(src, "MaterialSymbolsOutlined-VF.ttf")),
                                              {"FILL": 0, "GRAD": 0, "opsz": 20, "wght": 400})
    save_subset(icons, unicodes=[codepoints[n] for n in ICONS], path=os.path.join(OUT, "MaterialSymbolsOutlined-Subset.ttf"),
                family="Material Symbols Outlined")

    # 生成图标码表，供 Theme/Icons.xaml 对照
    with open(os.path.join(HERE, "icon-codepoints.txt"), "w", encoding="utf-8") as f:
        for name in ICONS:
            f.write(f"{name:22} &#x{codepoints[name]:X};\n")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
