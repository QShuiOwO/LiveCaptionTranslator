# NLLB local worker

This directory contains the local NLLB translation worker used by LiveCaptionTranslator.

## Recreate the Python environment

```powershell
conda env create -f .\tools\nllb\environment.yml
conda activate local-translator-nllb
```

LiveCaptionTranslator starts Python in this order:

1. `LCT_NLLB_PYTHON` environment variable
2. `D:\miniconda3\envs\local-translator-nllb\python.exe`
3. `python` or `py` from `PATH`

For a custom install location:

```powershell
$env:LCT_NLLB_PYTHON = "D:\miniconda3\envs\local-translator-nllb\python.exe"
```

## Required model files

The worker expects these paths:

```text
tools/nllb/nllb-200-distilled-600M-ct2/config.json
tools/nllb/nllb-200-distilled-600M-ct2/shared_vocabulary.json
tools/nllb/nllb-200-distilled-600M-ct2/model.bin
tools/nllb/tokenizer/tokenizer_config.json
tools/nllb/tokenizer/tokenizer.json
```

`model.bin` is intentionally ignored by Git because it is larger than GitHub's normal file size limit. Restore it from a release asset, shared artifact, or local CTranslate2 conversion before running translation.

## Manual smoke test

```powershell
$py = "D:\miniconda3\envs\local-translator-nllb\python.exe"
& $py .\tools\nllb\translate_worker.py `
  --model .\tools\nllb\nllb-200-distilled-600M-ct2 `
  --tokenizer .\tools\nllb\tokenizer `
  --device cpu `
  --compute-type int8
```

The worker prints a JSON `ready` line, then waits for JSON Lines requests on stdin.
