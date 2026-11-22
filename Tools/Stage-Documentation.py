"""Stage only the bilingual pages explicitly reviewed in the navigation manifest."""
import argparse
import json
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "docs/site"


def routes():
    manifest = json.loads((SOURCE / "_data/navigation.json").read_text(encoding="utf-8"))
    names = [item["path"] for group in manifest["groups"] for item in group["items"]]
    if len(names) != len(set(names)) or any(not re.fullmatch(r"[a-z0-9-]+\.html", name) for name in names):
        raise ValueError("Navigation must contain unique, safe page names")
    versions = json.loads((SOURCE / "releases/versions.json").read_text(encoding="utf-8"))
    if manifest["documentation_release"] not in versions:
        raise ValueError("The documentation baseline must have versioned release notes")
    for name in names:
        for language in ("", "ja"):
            if not (SOURCE / language / name.replace(".html", ".md")).is_file():
                raise ValueError(f"Missing bilingual page: {language}/{name}")
    return names


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", nargs="?", type=Path)
    parser.add_argument("--list-html", action="store_true")
    args = parser.parse_args()
    names = routes()
    if args.list_html:
        print(" ".join(names))
        return
    if args.destination is None:
        parser.error("destination is required")
    target = args.destination
    for name in names:
        for language in ("", "ja"):
            relative = Path(language) / name.replace(".html", ".md")
            content = (SOURCE / relative).read_text(encoding="utf-8")
            # Only these reviewed shared images live outside docs/site.
            for image in ("afterimage-gate-editor.png", "sprite-animation-en.png", "sprite-animation-ja.png"):
                before = ("../../images/" if language else "../images/") + image
                after = ("../images/" if language else "images/") + image
                content = content.replace(before, after)
            (target / relative).parent.mkdir(parents=True, exist_ok=True)
            (target / relative).write_text(content, encoding="utf-8")
    for relative in ("_data/navigation.json", "_includes/docs-navigation.html"):
        (target / relative).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(SOURCE / relative, target / relative)
    print(f"Staged {len(names)} reviewed pages in each language")


if __name__ == "__main__":
    main()
