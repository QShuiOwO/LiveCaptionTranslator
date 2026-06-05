import json
import sys


def translate(text, source_lang, target_lang):
    return text


def main():
    for line in sys.stdin:
        line = line.strip().lstrip("\ufeff")
        if not line:
            continue

        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            translated_text = translate(
                request.get("text", ""),
                request.get("source_lang", "auto"),
                request.get("target_lang", "zho_Hans"),
            )
            response = {
                "id": request_id,
                "translated_text": translated_text,
                "success": True,
                "error": "",
            }
        except Exception as exc:
            response = {
                "id": request_id,
                "translated_text": "",
                "success": False,
                "error": str(exc),
            }

        print(json.dumps(response, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
