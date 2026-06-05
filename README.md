# LiveCaptionTranslator

LiveCaptionTranslator 是一个基于 Windows Live captions 的本地实时翻译软件原型。

## 项目目标

核心流程：

1. Windows Live captions 负责系统音频识别并显示原文字幕。
2. LiveCaptionTranslator 通过 Windows UI Automation 读取 Live captions 窗口中的原文字幕。
3. 本地 Python translation worker 将原文翻译成中文。
4. 软件自己的 Overlay 悬浮字幕窗口显示译文。

## 当前状态

当前版本只创建最小 WPF 项目骨架：

- 主程序：`LiveCaptionTranslator.App`
- 主窗口：Live captions 状态、启动按钮、开始读取按钮、停止读取按钮、原文字幕显示区域、日志显示区域
- OverlayWindow：已创建空窗口，暂未接入逻辑
- 翻译功能、UI Automation 读取、Python worker 通信暂未实现

## 约束

- 不修改 Windows 系统文件
- 不注入 Live captions 进程
- 不替换 Live captions 内部字幕
- 不自己捕获系统音频
- 不使用 whisper.cpp
- 不依赖云服务
- 翻译层优先使用本地 Python worker

## 技术栈

- .NET 8
- WPF
- C#
- Windows UI Automation
- Python translation worker
- JSON Lines 通信
- x64

## 构建与运行

在 Windows 上执行：

```powershell
dotnet build
dotnet run --project .\LiveCaptionTranslator.App\LiveCaptionTranslator.App.csproj -p:Platform=x64
```
