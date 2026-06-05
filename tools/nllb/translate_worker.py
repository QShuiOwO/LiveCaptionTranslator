import argparse
import json
import re
import sys
import time
from typing import Iterable, List


def configure_stdio() -> None:
    try:
        sys.stdin.reconfigure(encoding="utf-8-sig")
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except AttributeError:
        pass


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def response(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def split_text(text: str, max_chars: int = 400) -> List[str]:
    text = re.sub(r"\s+", " ", text).strip()
    if not text:
        return [""]

    if len(text) <= max_chars:
        return [text]

    pieces: List[str] = []
    current = ""
    parts = re.split(r"(?<=[.!?。？！])\s+", text)

    for part in parts:
        if not part:
            continue

        if len(part) > max_chars:
            pieces.extend(split_by_space(part, max_chars))
            current = ""
            continue

        candidate = f"{current} {part}".strip() if current else part
        if len(candidate) <= max_chars:
            current = candidate
        else:
            if current:
                pieces.append(current)
            current = part

    if current:
        pieces.append(current)

    return pieces or [text[:max_chars]]


def split_by_space(text: str, max_chars: int) -> Iterable[str]:
    remaining = text.strip()
    while len(remaining) > max_chars:
        split_at = remaining.rfind(" ", 0, max_chars)
        if split_at < max_chars // 2:
            split_at = max_chars
        yield remaining[:split_at].strip()
        remaining = remaining[split_at:].strip()

    if remaining:
        yield remaining


def detect_source_language(text: str) -> str:
    if re.search(r"[\u3040-\u30ff]", text):
        return "jpn_Jpan"

    if re.search(r"[\u4e00-\u9fff]", text):
        return "zho_Hans"

    return "eng_Latn"


class NllbWorker:
    def __init__(self, model_path: str, tokenizer_path: str, device: str, compute_type: str) -> None:
        import ctranslate2
        from transformers import AutoTokenizer

        log(f"loading tokenizer: {tokenizer_path}")
        self.tokenizer = AutoTokenizer.from_pretrained(tokenizer_path)
        log(f"loading ctranslate2 model: {model_path}")
        self.translator = ctranslate2.Translator(model_path, device=device, compute_type=compute_type)
        log("worker ready")

    def translate(self, text: str, source_language: str, target_language: str) -> str:
        if not text.strip():
            return ""

        if source_language == "auto":
            source_language = detect_source_language(text)

        if source_language == target_language:
            return text.strip()

        translated_parts = [
            self.translate_chunk(chunk, source_language, target_language)
            for chunk in split_text(text)
            if chunk.strip()
        ]
        return " ".join(part for part in translated_parts if part).strip()

    def translate_chunk(self, text: str, source_language: str, target_language: str) -> str:
        self.tokenizer.src_lang = source_language
        token_ids = self.tokenizer.encode(text)
        source_tokens = self.tokenizer.convert_ids_to_tokens(token_ids)
        results = self.translator.translate_batch(
            [source_tokens],
            target_prefix=[[target_language]],
            beam_size=4,
        )
        output_tokens = results[0].hypotheses[0]
        if output_tokens and output_tokens[0] == target_language:
            output_tokens = output_tokens[1:]

        output_ids = self.tokenizer.convert_tokens_to_ids(output_tokens)
        return self.tokenizer.decode(output_ids, skip_special_tokens=True).strip()


def main() -> int:
    configure_stdio()

    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--tokenizer", required=True)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--compute-type", default="int8")
    args = parser.parse_args()

    try:
        worker = NllbWorker(args.model, args.tokenizer, args.device, args.compute_type)
        response({"type": "ready", "success": True})
    except Exception as exc:
        log(f"failed to initialize worker: {exc}")
        response({"type": "ready", "success": False, "error": str(exc)})
        return 2

    for line in sys.stdin:
        started_at = time.perf_counter()
        request_id = ""
        try:
            line = line.lstrip("\ufeff")
            request = json.loads(line)
            request_id = str(request.get("id", ""))
            text = str(request.get("text", ""))
            source_language = str(
                request.get("source_lang")
                or request.get("source_language")
                or "auto"
            )
            target_language = str(
                request.get("target_lang")
                or request.get("target_language")
                or "zho_Hans"
            )

            translated_text = worker.translate(text, source_language, target_language)
            response(
                {
                    "id": request_id,
                    "success": True,
                    "translated_text": translated_text,
                    "error": "",
                    "elapsed_ms": int((time.perf_counter() - started_at) * 1000),
                }
            )
        except Exception as exc:
            response(
                {
                    "id": request_id,
                    "success": False,
                    "translated_text": "",
                    "error": str(exc),
                    "elapsed_ms": int((time.perf_counter() - started_at) * 1000),
                }
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
