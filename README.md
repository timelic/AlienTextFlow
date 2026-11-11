# AlienTextFlow

《外星来信 / Message From Aliens》的译注字体 Mod，目标环境是 macOS 27、Apple Silicon 和游戏当前的 Unity 6000.6.8f1 Mono 版本。

当前版本：**0.2.0** · [下载与发布记录](https://github.com/timelic/AlienTextFlow/releases) · 代码采用 MIT 许可证，字体保留各自的 SIL OFL 许可证。当前安装器仅支持 macOS，Windows 尚未适配。

字格宽度、字高上限、外星字位置和原来的翻页／点击行为保留。短译注使用正常宽度；长译注使用字体真实 `wdth` 轴的 95、90、85、80、75 实例。单行达到压窄下限后仍需缩字号时，比较单行与两行：两行能明显增大实际字号、完整容纳文字且行间不重叠，才采用两行，否则保留单行。关闭 TMP 的几何横向压缩，不通过 Transform 拉扁字形。

两行只改变显示布局，不向译注字符串插入换行，不设置会截断输入的 `lineLimit=2`。英文优先在空格处断行，过长单词可折行，中文沿用 TMP 的断行规则。用实际字形边界收紧多余行距，并校正输入光标、选区和高度缓存，避免字体较大的 ascender/descender 行框把内容滚出原输入框。Enter 保持提交译注的行为。

中文使用 **文渊黑体 Flex**，英文和数字使用 **Mona Sans**。字体固定在 400 字重；Mona Sans 的光学字号轴固定为 20。文渊 Flex 的 `HGHT` 轴保持默认值，确保不会为了压窄而改变字高。Unity 的 TMP 文件路径 API 不暴露可变字体轴，因此构建阶段用 fontTools 生成六档静态实例，运行时按实际排版宽度选最宽的可容纳实例。实例使用 `ATF` 家族名；许可证与来源哈希随包保存。

## 下载与安装

安装需要 Python 3.10+ 和 .NET SDK 10。下载 [0.2.0 完整包](https://github.com/timelic/AlienTextFlow/releases/download/v0.2.0/AlienTextFlow-0.2.0-macOS.zip)，解压后进入其中的 `AlienTextFlow` 目录，退出游戏，再运行：

```sh
python3 scripts/manage.py install
python3 scripts/manage.py status
```

发布包已包含字体和编译好的 Mod，无需重新生成字体或编译。重新启动游戏生效。默认游戏路径是 `~/Library/Application Support/Steam/steamapps/common/Message From Aliens/mac.app`；自定义路径时，在安装、状态检查或卸载命令后加上 `--game "/path/to/mac.app"`。

## 从源码构建

需要 .NET SDK 10、Python 3.10+ 和本机安装的游戏。仓库已经包含生成的字体实例；直接构建即可。首次还原 Mono.Cecil 依赖需要网络。构建引用本机游戏程序集，但不把它们打进发布包。

```sh
git clone https://github.com/timelic/AlienTextFlow.git
cd AlienTextFlow
python3 scripts/manage.py build
```

只有重新生成字体时才需要 fontTools 和字体源文件下载：

```sh
python3 -m venv work/venv
work/venv/bin/pip install 'fonttools==4.68.0'
work/venv/bin/python scripts/prepare_fonts.py
```

游戏不在默认 Steam 目录时，使用 `dotnet build ... -p:GameApp="/path/to/mac.app"` 传入引用路径。

## 安装与还原

退出游戏后执行：

```sh
python3 scripts/manage.py install
python3 scripts/manage.py status
```

还原原版：

```sh
python3 scripts/manage.py uninstall
```

安装器只在 `Draw.Awake` 与 `AlienCharacter.UpdatePos` 接入托管回调；不安装 BepInEx、Doorstop 或原生启动器，也不增加系统安全权限／JIT entitlement。它保存原程序集的 SHA-256 和备份，安装后重新以原有 entitlement 做 ad-hoc 签名。备份哈希不符时拒绝覆盖；游戏更新后，以新版程序集另存备份并重新检查补丁位置，绝不把旧版程序集恢复到新版上。Steam 校验文件会移除程序集补丁，需要在更新后重新构建并安装。

Mod 不读写 `.sav`，不会改变云存档平台标记，也不会修复 Steam 跨平台云同步。

## 配置

安装后配置在游戏 `mac.app/Contents/Resources/Data/Managed/AlienTextFlow/config.json`，修改后重启游戏。

- `Enabled`：关闭后使用原版行为。
- `MinWidthPercent`：压窄下限，允许 75、80、85、90、95、100。
- `FontSizeMultiplier`：基础字号倍率，0.5–2；默认 1。
- `MinFontSizePixels`：极长译注的最终字号下限，默认 1。译注本身最多 49 个字符，保持游戏原限制。
- `AllowTwoLines`：允许在单行需要缩字时比较两行布局，默认开启。
- `TwoLineMinimumGain`：两行相对单行所需的最低字号收益，默认 1.12，即至少增大约 12% 才换行；范围 1–2。

日志在 `~/Library/Logs/AlienTextFlow/mod.log`。0.1.1 使用构建时提取的字体 advance 数据预测宽度，只对候选实例做 TMP 核验；相同译注跨字框共享选择结果，每个字框另有快速缓存。缺失字形按字符串批量生成，不再逐字调用字体引擎；中文 SC 译注关闭不需要的动态 GSUB 查询，英文保留 kerning。输入变化或窗口变化时重新计算，不每帧测量全部字体。

文渊 Flex 原字体的 `HGHT` 轴范围约为 66.7–100，Mona Sans 无独立高度轴。实测中文 HGHT 压缩到 80% 时，TMP 采用的行框高度仍然不变，单独压字形不会自动释放行框空间。因此保持纵轴 100%，优先减少行间留白并比较实际可读字号，避免中英文混排时只有中文被压矮。

## 验证

```sh
python3 scripts/manage.py self-test
```

该命令创建 APFS 克隆游戏，在克隆中运行 Unity 批处理测试。显式自测参数会在 `Draw.Awake` 的最前面跳过游戏初始化，因此测试不加载 Steam 或玩家存档。报告在 `work/self-test/report.json`，包括实际字体宽度档、最终字号、两行边界及间隙、原游戏输入框的完整字符串／光标检查、中文字形回退、缺字数量和性能数据。自测目录存在时不会静默覆盖。

参考：[BazaarPlusPlus](https://github.com/BazaarPlusPlus/BazaarPlusPlus) 的 macOS 加载经验。此处采用更小的托管程序集补丁，并未复制它的启动器代码。

字体来源：[文渊字体 v1.110](https://github.com/takushun-wu/WenYuanFonts/releases/tag/v1.110)、[Mona Sans v2.0.27](https://github.com/github/mona-sans/releases/tag/v2.0.27)。字体受各自 SIL OFL 条款约束；本项目代码见 `LICENSE`。
