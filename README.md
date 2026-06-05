# LiveCaptionTranslator

LiveCaptionTranslator 是一个基于 Windows Live captions 的本地实时翻译软件原型。

## 项目目标

核心流程：

1. Windows Live captions 负责系统音频识别并显示原文字幕。
2. LiveCaptionTranslator 通过 Windows UI Automation 读取 Live captions 窗口中的原文字幕。
3. 本地 Python translation worker 将原文翻译成中文。
4. 软件自己的 Overlay 悬浮字幕窗口显示译文。

## 当前状态

当前版本实现了 v0.3.1 原型链路：

- 主程序：`LiveCaptionTranslator.App`
- 主窗口：Live captions 状态、读取控制、字幕分段、翻译状态、翻译结果、日志
- Live captions 读取：通过 Windows UI Automation 读取实时字幕窗口文本
- 字幕分段：对 Live captions 增量刷新文本进行去重和稳定分段
- 翻译队列：稳定片段按顺序进入本地翻译队列，支持缓存、超时和队列长度限制
- Python worker：通过 stdin/stdout JSON Lines 与本地 worker 通信
- OverlayWindow：已创建空窗口，暂未接入逻辑

## Python worker

当前仓库包含本地 NLLB worker：

```text
tools/nllb/translate_worker.py
```

它会按 JSON Lines 协议读取请求，加载本地 CTranslate2 NLLB 模型并返回译文。项目不会自动下载模型，也不会访问网络。

当前 C# 程序会优先使用以下 conda 环境启动 worker：

```text
D:\miniconda3\envs\local-translator-nllb\python.exe
```

也可以通过环境变量覆盖 Python 路径：

```powershell
$env:LCT_NLLB_PYTHON = "D:\miniconda3\envs\local-translator-nllb\python.exe"
```

本地模型和 tokenizer 路径：

```text
tools/nllb/nllb-200-distilled-600M-ct2
tools/nllb/tokenizer
```

`tools/nllb/nllb-200-distilled-600M-ct2/model.bin` 超过 GitHub 普通文件大小限制，已被 `.gitignore` 排除。复现时需要从 release artifact、共享文件或本地 CTranslate2 转换结果恢复到该路径。

Python 环境可用下面的文件创建：

```powershell
conda env create -f .\tools\nllb\environment.yml
```

请求格式：

```json
{"id":"...","text":"...","source_lang":"auto","target_lang":"zho_Hans"}
```

响应格式：

```json
{"id":"...","translated_text":"...","success":true,"error":""}
```

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
