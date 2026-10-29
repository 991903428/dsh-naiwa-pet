"""Validate market/index.json against the format-1 rules DSH parses.

Rules mirrored from @deepseek-ai/dsh-plugin-market-index (lib/index.js):
  * top level: format === 1, title is a localized string (string, or object with
    at least one of "en"/"zh"), description/metadata optional
  * metadata.categories[]: { id, title(localized) }
  * plugins[]: { name(valid npm name), title/description/icon/author/homepage/
    repository optional, metadata.category?, versions[] non-empty }
  * versions[]: { version(exact semver), publishTimestamp(int seconds),
    source: {type:'npm'|'git', ...}, engines?: {dsh?: semver range} }
  * git source: commit must be 40 lowercase hex, url must be https with at least
    two path segments, optional path
  * versions are sorted descending by semver; duplicate name/version rejected
"""
from __future__ import annotations

import json
import re
import sys
from urllib.parse import urlparse

SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
                    r"(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)"
                    r"(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
                    r"(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$")
NPM_NAME = re.compile(r"^(?:@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*$")
HEX40 = re.compile(r"^[0-9a-f]{40}$")

errors: list[str] = []


def check(ok: bool, path: str, message: str) -> None:
    if not ok:
        errors.append(f"{path}: {message}")


def localized(value, path: str) -> None:
    if isinstance(value, str):
        if not value.strip():
            errors.append(f"{path}: empty string")
        return
    check(isinstance(value, dict), path, "must be a string or a locale object")
    if isinstance(value, dict):
        check(bool(value.get("en") or value.get("zh")), path, 'needs an "en" or "zh" entry')


def main(path: str) -> int:
    with open(path, encoding="utf-8") as handle:
        index = json.load(handle)

    check(index.get("format") == 1, "format", "must be 1")
    localized(index.get("title"), "title")
    if "description" in index:
        localized(index["description"], "description")

    metadata = index.get("metadata") or {}
    categories = {c.get("id") for c in metadata.get("categories", []) if isinstance(c, dict)}
    for i, category in enumerate(metadata.get("categories", [])):
        check(bool(category.get("id")), f"metadata.categories[{i}].id", "required")
        localized(category.get("title"), f"metadata.categories[{i}].title")

    seen: set[tuple[str, str]] = set()
    for i, plugin in enumerate(index.get("plugins", [])):
        base = f"plugins[{i}]"
        name = plugin.get("name", "")
        check(bool(NPM_NAME.match(name)), f"{base}.name", f"invalid npm name {name!r}")
        for key in ("title", "description"):
            if key in plugin:
                localized(plugin[key], f"{base}.{key}")
        if "icon" in plugin:
            icon = plugin["icon"]
            check(icon.startswith(("https://", "data:")) or icon.startswith("/"),
                  f"{base}.icon", "must be https, absolute path or data: URL")
            if icon.startswith("data:"):
                check(len(icon) <= 32 * 1024 + 64, f"{base}.icon", "data URL over 32 KiB")
        category = (plugin.get("metadata") or {}).get("category")
        if category is not None:
            check(category in categories, f"{base}.metadata.category", "must exist in metadata.categories")

        versions = plugin.get("versions") or []
        check(bool(versions), f"{base}.versions", "at least one version")
        previous = None
        for j, version in enumerate(versions):
            vbase = f"{base}.versions[{j}]"
            raw = version.get("version", "")
            check(bool(SEMVER.match(raw)), f"{vbase}.version", f"not exact semver: {raw!r}")
            key = (name, raw)
            check(key not in seen, f"{vbase}.version", "duplicate name/version")
            seen.add(key)
            if previous is not None and SEMVER.match(raw) and SEMVER.match(previous):
                now = tuple(int(p) for p in raw.split("-")[0].split("."))
                was = tuple(int(p) for p in previous.split("-")[0].split("."))
                check(now < was, f"{vbase}.version", "versions must be sorted descending")
            previous = raw
            stamp = version.get("publishTimestamp")
            check(isinstance(stamp, int) and stamp >= 0, f"{vbase}.publishTimestamp", "unix seconds")
            source = version.get("source") or {}
            kind = source.get("type")
            check(kind in ("npm", "git"), f"{vbase}.source.type", "must be npm or git")
            if kind == "git":
                url = source.get("url", "")
                parsed = urlparse(url)
                check(parsed.scheme == "https", f"{vbase}.source.url", "must be https")
                segments = [s for s in parsed.path.split("/") if s]
                check(len(segments) >= 2, f"{vbase}.source.url", "needs owner/repo path")
                check(bool(HEX40.match(source.get("commit", ""))), f"{vbase}.source.commit",
                      "40 lowercase hex")
                if "path" in source:
                    check(source["path"].startswith("/"), f"{vbase}.source.path", "must start with /")
            engines = version.get("engines") or {}
            if "dsh" in engines:
                check(isinstance(engines["dsh"], str) and bool(engines["dsh"].strip()),
                      f"{vbase}.engines.dsh", "semver range string")

    for problem in errors:
        print("ERROR", problem)
    print("OK" if not errors else f"{len(errors)} problem(s)")
    return 0 if not errors else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1] if len(sys.argv) > 1 else "market/index.json"))
