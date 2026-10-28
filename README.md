# Manga Reader

一个轻量的 Windows 漫画阅读器，**直接读取 MOBI / AZW / AZW3 文件里内嵌的图片**。

![连续卷动：章节之间无空白](screenshots/continuous-gapless.png)

---

## 特点

- **真正的轻量**：整个程序就是一个 54 KB 的 `Manga Reader.exe`，基于系统自带的 .NET Framework 4.x + WPF，
  不需要安装 .NET SDK、Python、Node 或任何第三方运行库。
- **支持 MOBI 系列格式**：`.mobi` / `.azw` / `.azw3` / `.prc`，包括 MOBI6、KF8(AZW3)，
  以及 MOBI6 + KF8 的双格式合订本。
- **两种阅读方向**：
  - **从右往左（默认）** —— 日式漫画的阅读顺序，双页时前一页在右边；
  - **从左往右** —— 西式漫画 / 条漫。
  方向同时决定 ← → 方向键和点击画面的翻页方向。
- **三种显示模式**：
  - **连续卷动**（默认）—— 页面首尾相接，页与页之间**没有任何留白**；
  - **单页**；**双页对开**（自动按阅读方向排列左右页）。
- **章节之间没有空白**：一次可以选中多个文件，按顺序无缝拼成一本书，
  章节交界处直接是上一章的最后一页接下一章的第一页，不会插入空白页或空隙。
- **全屏**：`F11` 进入 / 退出，全屏时工具栏自动隐藏，鼠标一动就出来，停下 2.6 秒又自动隐去
  （连鼠标指针一起隐藏）。
- **一键隐藏工具栏**：`Tab`（或 `H`）让顶部工具栏和底部提示条一起消失，进入完全没有干扰的阅读画面；
  再按一次就回来。这个状态会记住，下次打开仍然是无干扰模式，画面下方会有一条提示告诉你怎样恢复。
- **缩放**：`Ctrl` + 滚轮，或 `+` `-` `0`，`50%`–`300%`，**三种模式都能用**；
  连续卷动下放到 100% 以上会自动出现横向滚动条，`Shift` + 滚轮可以左右平移看细节。
- 其它：拖放打开、章节下拉跳转、记忆上次的方向 / 模式 / 缩放 / 工具栏状态、
  图片解压缓存（第二次打开秒开）。

![双页对开，从右往左]

---

## 快速开始

1. 双击 **`Manga Reader.exe`**，点左上角「打开文件」；
2. 或者把 `.mobi` 文件**直接拖进窗口**；
3. 或者把文件拖到 **`MangaReader.bat`** 上（源码改动后会自动重新编译再启动）；
4. 或者命令行：`"Manga Reader.exe" "D:\comics\vol1.mobi"`。

> 想一次读一整套？在打开对话框里按住 Ctrl 多选，或用鼠标框选，
> 它们会按选择顺序拼成一本书，「章节」下拉框可以在文件之间跳转。

---

## 快捷键

| 按键 | 作用 |
| --- | --- |
| `←` `→` | 按当前阅读方向翻页 |
| `空格` / `PageDown` / `Enter` | 下一页 |
| `PageUp` / `Backspace` | 上一页 |
| `↑` `↓` | 连续卷动模式下小幅滚动；单页 / 双页模式下翻页 |
| `Home` / `End` | 第一页 / 最后一页 |
| 单击画面左 / 右半边 | 翻页（方向跟着阅读方向走） |
| `D` | 切换「从右往左 / 从左往右」 |
| `1` `2` `3` | 连续卷动 / 单页 / 双页 |
| `Ctrl` + 滚轮 | 缩放（三种模式都可用） |
| `Shift` + 滚轮 | 连续卷动模式下左右平移（放大后看细节） |
| `+` `-` `0` | 放大 / 缩小 / 恢复适应页面 |
| `Tab` 或 `H` | 隐藏 / 显示工具栏（无干扰阅读） |
| `F11` | 全屏 / 退出全屏 |
| `Esc` | 恢复工具栏 → 退出全屏 / 关掉帮助 |
| `F1` | 显示 / 隐藏快捷键帮助 |
| `☰` 菜单 | 打开文件、全屏、隐藏工具栏、清空图片缓存、关于 |

![单页，从左往右]

---

## 缩放是怎么算的

| 选项 | 连续卷动模式 | 单页 / 双页模式 |
| --- | --- | --- |
| 适应页面 | 等同「适应宽度」（纵向是一整条，没有"页"可适应高度） | 整页缩放到刚好放得下 |
| 适应宽度 | 页面宽度 = 窗口宽度 | 页面宽度 = 窗口宽度，上下滚动 |
| 50% – 300% | 页面宽度 = 窗口宽度 × 百分比 | 页面宽度 = 图片原始像素 × 百分比 |

超过 100% 时页面宽于窗口，底部会出现横向滚动条；缩小到 100% 以下时页面居中，
上下方向依旧严丝合缝。缩放结果同样会被记住。

![连续卷动 150%]

---

## 支持范围与限制

- **只支持图片型的 MOBI**（也就是漫画）。纯文字的电子书里没有图片，程序会给出提示。
- **不支持带 DRM 加密的 azw / azw3**（Kindle 商店买的加密书），程序会明确提示是 DRM 导致。
- 图片记录里的 JPEG / PNG / GIF / BMP 都能识别；WebP 只在能拿到尺寸时才处理。
- 首次打开某个文件时会把内嵌图片解压到缓存，之后打开是秒开：
  `%LOCALAPPDATA%\MangaReader\cache\<文件指纹>\`
  想清理就点工具栏的 `☰` → 「清空图片缓存」。

---

## 目录结构

```
Manga Reader\
├─ Manga Reader.exe          编译好的程序，双击即用
├─ MangaReader.bat           启动器：源码比 exe 新时自动重新编译
├─ build.cmd                 只编译，不启动
├─ source\                  全部源码
│   ├─ Mobi.cs               PalmDB / MOBI / KF8 解析 + 图片提取
│   ├─ Reader.cs             WPF 界面、翻页、全屏、缩放
│   └─ README.md             源码说明与手工编译命令
├─ tools\
│   └─ make_test_mobi.py     生成测试用的 MOBI / AZW3 样例
├─ samples\                  合成出来的测试样例（可以删）
└─ screenshots\              README 里的截图
```

---

## 从源码重新编译

源码全部放在 **`source\`** 文件夹里（只有两个 `.cs` 文件，没有任何第三方依赖）。
在项目根目录双击 **`build.cmd`** 即可重新编译，它调用的是 Windows 自带的 C# 编译器：

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

所以只要系统里有 .NET Framework 4.x（Windows 10 / 11 默认都有）就能编译，不需要装 Visual Studio。
也可以把 `source\` 单独拷走，见 `source\README.md` 里的手工编译命令。

---

## 实现要点

**MOBI 解析**（`source/Mobi.cs`）

- 读 PalmDB 头拿到记录表（每条记录的偏移和长度），记录 0 里是 PalmDOC 头 + MOBI 头。
- 从 MOBI 头偏移 `0x5C` 取「First Image Index」，往后的记录按**文件魔数**判断是不是图片
  （`FF D8 FF` / `89 50 4E 47` / `GIF8` / `BM`），因此 INDX、FLIS、FCIS、FONT、HUFF 这些
  非图片记录会被自动跳过。
- 同时解析 EXTH：`201` 封面偏移、`121` KF8 分界记录、`503` 书名、`100` 作者。
- **双格式合订本**（MOBI6 + KF8）：如果 EXTH 121 指向的 KF8 头里 First Image Index 落在分界点之后，
  就整段使用 KF8 部分的高清页面；否则只扫到分界点为止。
  这样既拿到清晰度更好的那一份，又不会把同一本书的图片印两遍。
- 图片记录尾部常带填充或额外的 EOF 标记，按格式裁掉：
  JPEG 取最后一个 `FF D9`、PNG 取 `IEND`、GIF 取 `3B`、BMP 按头部里的长度字段。
- 尺寸直接从图片文件头解析（JPEG 扫 SOF 段，PNG 读 IHDR，GIF / BMP 读头），不需要解码整张图。

**界面与性能**（`source/Reader.cs`）

- 连续卷动用 `ListBox` + `VirtualizingStackPanel`（`VirtualizationMode=Recycling`、
  `ScrollUnit=Pixel`），只实例化视口附近的页面。
- 每页宽度绑定在 `MultiBinding(ScrollViewer.ViewportWidth, Window.ContinuousZoom)` 上，
  改一次缩放就整条重算，滚动条、页码、跳页始终对得上。
- `ListBoxItem` 的模板被换成裸的 `ContentPresenter`，`Margin` / `Padding` / `BorderThickness` 全部为 0，
  图片 `Margin=0` 且宽度绑定到 `ScrollViewer.ViewportWidth` —— 这就是**页面之间零间隙**的来源。
- 每页的显示高度 = 视口宽度 × 原始宽高比，累加成前缀和数组，
  于是「滚动位置 ↔ 页码」可以精确换算，跳页 / 章节跳转都定位到页面顶部。
- 解码后的位图放在一个 14 条的 LRU 里，并按显示宽度 `DecodePixelWidth` 解码，内存占用不会随
  整本书增长。
- 全屏 = `WindowStyle=None` + `WindowState=Maximized`。
- 工具栏是否显示由两个开关一起决定：`_chromeHidden`（用户手动隐藏，会写进设置）和
  `_autoHidden`（全屏空闲自动隐藏），`UpdateChrome()` 是唯一的出口，不会互相打架：
  手动隐藏时鼠标移动也不会把工具栏弹出来。

**自检**

程序内置两个无界面模式，方便批量验证（不会弹窗）：

```
Manga Reader.exe --selftest  <书.mobi> <报告.txt>
Manga Reader.exe --render-ui <书.mobi> <输出.png> [宽] [高] [cont|single|double] [rtl|ltr] [页] [额外滚动]
```

---

## 测试样例

`samples\` 里的文件是用 `tools/make_test_mobi.py` 合成的（需要 Pillow 才能重新生成）：

| 文件 | 说明 |
| --- | --- |
| `Sample Manga (MOBI6).mobi` | 30 页，MOBI6，文字用 PalmDOC 压缩 |
| `Sample Manga (KF8).azw3` | 16 页，KF8 / AZW3，高清页面 |
| `Sample Manga (uncompressed).mobi` | 12 页，文字不压缩 |
| `Sample Dual Format.mobi` | MOBI6 + KF8 双格式合订本（用于验证不会重复出图） |

```
python tools\make_test_mobi.py samples
```
