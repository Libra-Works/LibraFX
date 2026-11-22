"""Stage explicitly listed bilingual release notes, or check their rendered links."""

import argparse
from html.parser import HTMLParser
import json
from pathlib import Path
import re
import shutil
from urllib.parse import urljoin


class PageLinks(HTMLParser):
    def __init__(self, text):
        super().__init__()
        self.links = set()
        self.language = None
        self.feed(text)

    def handle_starttag(self, tag, attributes):
        attributes = dict(attributes)
        if tag == "html":
            self.language = attributes.get("lang")
        if tag == "a" and "href" in attributes:
            self.links.add(attributes["href"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--verify-rendered", action="store_true")
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1] / "docs/site"
    versions = json.loads((source / "releases/versions.json").read_text(encoding="utf-8-sig"))
    if not isinstance(versions, list) or not versions or any(
        not isinstance(version, str) or not re.fullmatch(r"[0-9]{4}\.(?:[1-9]|1[0-2])\.[1-9][0-9]*", version)
        for version in versions
    ):
        raise ValueError("Release versions must be a nonempty list of year.month.build identifiers.")
    if len(set(versions)) != len(versions) or versions != sorted(
        versions, key=lambda version: tuple(map(int, version.split("."))), reverse=True
    ):
        raise ValueError("List each released version once, newest first.")

    for prefix, language, other in [("", "en", "ja/"), ("ja/", "ja", "")]:
        if any((args.destination / f"{prefix}preview-update{extension}").exists() for extension in [".md", ".html"]):
            raise ValueError("Use a fresh staging directory: the removed preview-update page is still present.")
        if args.verify_rendered:
            index = PageLinks((args.destination / f"{prefix}release-notes.html").read_text(encoding="utf-8"))
        else:
            index = (source / f"{prefix}release-notes.md").read_text(encoding="utf-8-sig")
        for version in versions:
            relative = f"{prefix}releases/{version}"
            if args.verify_rendered:
                page = PageLinks((args.destination / f"{relative}.html").read_text(encoding="utf-8"))
                expected = f"/LibraFX/{relative}.html"
                counterpart = f"/LibraFX/{other}releases/{version}.html"
                index_links = {urljoin(f"/LibraFX/{prefix}release-notes.html", link) for link in index.links}
                page_links = {urljoin(expected, link) for link in page.links}
                if (index.language != language or page.language != language or expected not in index_links or
                        counterpart not in page_links or f"/LibraFX/{prefix}release-notes.html" not in page_links):
                    raise ValueError(f"Missing release navigation, language or counterpart: {relative}")
            else:
                if f"(releases/{version}.md)" not in index:
                    raise ValueError(f"Add {version} to the {language} release index.")
                page = source / f"{relative}.md"
                if not re.search(rf'^release_version:\s*[\"\']?{re.escape(version)}[\"\']?\s*$',
                                 page.read_text(encoding="utf-8-sig"), re.MULTILINE):
                    raise ValueError(f"Release frontmatter does not match its version: {page}")
                target = args.destination / f"{relative}.md"
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(page, target)
    print(f"{'Verified' if args.verify_rendered else 'Staged'} {len(versions)} versioned releases in English and Japanese.")


if __name__ == "__main__":
    main()
