"""Check staged Jekyll output, including local images, links and heading anchors."""
import argparse
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urljoin, urlsplit


class Page(HTMLParser):
    def __init__(self, text):
        super().__init__(convert_charrefs=True)
        self.links = []
        self.ids = set()
        self.lang = None
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "html":
            self.lang = attrs.get("lang")
        if "id" in attrs:
            self.ids.add(attrs["id"])
        for key in ("href", "src"):
            if attrs.get(key):
                self.links.append(attrs[key])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path)
    args = parser.parse_args()
    root = args.site.resolve()
    pages = {p.relative_to(root).as_posix(): Page(p.read_text(encoding="utf-8")) for p in root.rglob("*.html")}
    if not pages:
        parser.error("No rendered HTML found")
    errors = []
    for name, page in pages.items():
        expected = "ja" if name.startswith("ja/") else "en"
        if page.lang != expected:
            errors.append(f"{name}: expected language {expected}")
        for link in page.links:
            url = urlsplit(urljoin("https://documentation.invalid/LibraFX/" + name, link))
            if url.netloc != "documentation.invalid" or url.scheme not in ("http", "https"):
                continue
            path = unquote(url.path)
            if not path.startswith("/LibraFX/"):
                errors.append(f"{name}: local URL escapes site base: {link}")
                continue
            target = path[len("/LibraFX/"):]
            if not target or target.endswith("/"):
                target += "index.html"
            if not (root / target).is_file():
                errors.append(f"{name}: missing target {link}")
            elif url.fragment and target in pages and unquote(url.fragment) not in pages[target].ids:
                errors.append(f"{name}: missing anchor {link}")
    if errors:
        raise SystemExit("\n".join(sorted(set(errors))))
    print(f"Verified local links, images, anchors and languages across {len(pages)} rendered pages")


if __name__ == "__main__":
    main()
