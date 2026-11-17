#!/usr/bin/env python3
"""Writes the third-party notices of a `dotnet publish` folder, and fails on a
license that could keep Acrux out of Fedora's Copr.

    python3 scripts/third_party_notices.py <publish-dir> [--spdx]

Reads the folder's *.deps.json, the exact list of NuGet packages (and the .NET
runtime pack) whose files went into it, and the packages themselves from the
NuGet cache the restore just filled ($NUGET_PACKAGES or ~/.nuget/packages): no
network. Writes into the folder:

- THIRD-PARTY-NOTICES.txt: each package, its version, license and copyright,
  then the license texts. A package's own license and notice files go in as
  they are; a package that only declares its license gets the standard text,
  with its copyright line. Identical texts appear once;
- THIRD-PARTY-NOTICES-polars.html: the Rust crates inside Polars' native
  library, as Polars.NET's author generates the list.

Every license must be on ALLOWED, the licenses Fedora accepts for code (Copr
only takes material under a license on Fedora's list). A package with a license
outside it, without a license text, or a Polars crate under another license
stops the build, so a new dependency is caught in the pull request instead of
at release time. --spdx prints the third-party licenses as one SPDX expression
(the RPM's License: field adds Acrux's own MIT and the logo's CC-BY-ND-4.0).
"""

import hashlib
import html
import json
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent / "installer" / "third-party"

# Status "allowed" in Fedora's license data
# (forge.fedoraproject.org/legal/fedora-license-data), checked on 2026-11-17.
# Before adding one, check it there: "allowed-content" or "allowed-documentation"
# isn't enough for code.
ALLOWED = {
    "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "BSL-1.0",
    "CDLA-Permissive-2.0", "ISC", "MIT", "MPL-2.0", "Unicode-3.0", "Zlib",
}

# Fedora allows CC0-1.0 on code only where it was before 2022-08-01, and ships
# this crate itself (rust-tiny-keccak, CC0-1.0, in Fedora 44).
CRATE_EXCEPTIONS = {"tiny-keccak": "CC0-1.0"}

# Packages whose nuspec doesn't say enough: a license file without an SPDX
# expression, or an expression without a license text or copyright.
OVERRIDES = {
    "Avalonia.Angle.Windows.Natives": {"license": "BSD-3-Clause"},
    # the four Polars.NET packages ship only the crate list; the wrapper's own
    # MIT license is in the author's repository (ErrorLSC/Polars.NET)
    "Polars.NET": {"text": "Polars.NET-LICENSE.txt"},
}

STANDARD_TEXTS = {"MIT": "MIT.txt", "Apache-2.0": "Apache-2.0.txt"}
MIT_COPYRIGHT_SLOT = "Copyright (c) <year> <copyright holders>"
NOTICE_FILE = re.compile(
    r"^(licen[cs]e|notice|third-?party-?notices)(\.(txt|md|rtf))?$", re.I)
POLARS_HTML = "THIRD_PARTY_LICENSES.html"


def override(name):
    for prefix, value in OVERRIDES.items():
        if name == prefix or name.startswith(prefix + "."):
            return value
    return {}


def nuspec_field(nuspec, tag):
    m = re.search(rf"<{tag}(?:\s[^>]*)?>([^<]*)</{tag}>", nuspec)
    return html.unescape(m.group(1)).strip() if m else ""


def license_ids(expression):
    """The license ids of an SPDX expression; for 'A OR B', any one is enough."""
    if " OR " in expression and not re.search(r"[()]| AND | WITH ", expression):
        return [i.strip() for i in expression.split(" OR ")], "any"
    ids = re.split(r"\s+(?:AND|OR|WITH)\s+|[()]", expression)
    return [i.strip() for i in ids if i.strip()], "all"


def allowed(expression):
    ids, mode = license_ids(expression)
    hits = [i in ALLOWED for i in ids]
    return bool(ids) and (any(hits) if mode == "any" else all(hits))


def rtf_to_text(rtf):
    """Enough RTF for the Roslyn notice: paragraphs, escaped characters, and
    the text of hyperlinks; font tables and other destinations are dropped."""
    out, stack, skip = [], [], False
    i = 0
    tokens = re.compile(r"\\([a-z]+)(-?\d+)? ?|\\'([0-9a-f]{2})|\\([^a-z])|([{}])|([^\\{}\r\n]+)|[\r\n]+", re.I)
    skip_words = {"fonttbl", "colortbl", "stylesheet", "info", "fldinst", "generator", "pict"}
    while i < len(rtf):
        m = tokens.match(rtf, i)
        i = m.end()
        word, num, hexchar, symbol, brace, text = m.groups()
        if brace == "{":
            stack.append(skip)
        elif brace == "}":
            skip = stack.pop() if stack else False
        elif skip:
            continue
        elif word:
            if word in skip_words:
                skip = True
            elif word in ("par", "line"):
                out.append("\n")
            elif word == "tab":
                out.append("\t")
            elif word == "u" and num:
                out.append(chr(int(num) % 65536))
                if rtf[i:i + 1] == "?":
                    i += 1
        elif hexchar:
            out.append(bytes([int(hexchar, 16)]).decode("cp1252", "replace"))
        elif symbol == "*":
            skip = True
        elif symbol in ("\\", "{", "}"):
            out.append(symbol)
        elif text:
            out.append(text)
    return re.sub(r"\n{3,}", "\n\n", "".join(out)).strip() + "\n"


def read_text(path):
    data = path.read_bytes()
    if path.suffix.lower() == ".rtf":
        text = rtf_to_text(data.decode("cp1252", "replace"))
    else:
        text = data.decode("utf-8-sig", "replace").replace("\r\n", "\n")
    # the Roslyn RTF ends in a NUL, which makes grep call the whole file binary;
    # form feeds stay (the GPL-family texts use them as page breaks)
    return re.sub(r"[\x00-\x08\x0b\x0d-\x1f]", "", text).rstrip() + "\n"


def polars_crates(page):
    """(crate, SPDX id) for each crate in the cargo-about page."""
    sections = re.findall(
        r'<h3 id="([^"]+)">[^<]*</h3>\s*<h4>Used by:</h4>\s*<ul class="license-used-by">(.*?)</ul>',
        page, re.S)
    if not sections:
        raise SystemExit(f"{POLARS_HTML}: no license sections found; did its format change?")
    return [(html.unescape(crate), spdx) for spdx, crates in sections
            for crate in re.findall(r">([^<]+)</a>", crates)]


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 1:
        raise SystemExit(__doc__)
    publish = Path(args[0])
    deps = list(publish.glob("*.deps.json"))
    if len(deps) != 1:
        raise SystemExit(f"{publish}: expected one *.deps.json (a folder publish, not single-file); found {len(deps)}")
    cache = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")

    packages = []
    for key, lib in json.loads(deps[0].read_text(encoding="utf-8-sig"))["libraries"].items():
        if lib["type"] not in ("package", "runtimepack"):
            continue
        name, version = key.split("/")
        name = name.removeprefix("runtimepack.")
        folder = cache / name.lower() / version.lower()
        if not folder.is_dir():
            raise SystemExit(f"{name} {version}: not in the NuGet cache at {cache}; restore first")
        packages.append((name, version, folder))
    packages.sort(key=lambda p: p[0].lower())

    problems, rows, blocks, spdx = [], [], {}, set()
    for name, version, folder in packages:
        nuspec = next(folder.glob("*.nuspec")).read_text(encoding="utf-8-sig")
        extra = override(name)
        declared = re.search(r'<license type="expression">([^<]*)</license>', nuspec)
        expression = extra.get("license") or (declared.group(1).strip() if declared else "")
        copyright_ = nuspec_field(nuspec, "copyright")
        if not expression:
            problems.append(f"{name} {version}: no SPDX license in its nuspec (a license file or URL); "
                            "read it and add it to OVERRIDES")
            continue
        if not allowed(expression):
            problems.append(f"{name} {version}: license {expression} is not on ALLOWED (Fedora's list)")
        spdx.update(license_ids(expression)[0])

        texts = [read_text(f) for f in sorted(folder.iterdir())
                 if f.is_file() and NOTICE_FILE.match(f.name)]
        if not texts and "text" in extra:
            texts = [read_text(HERE / extra["text"])]
        if not texts and expression in STANDARD_TEXTS:
            standard = read_text(HERE / STANDARD_TEXTS[expression])
            if expression == "MIT":
                if not copyright_:
                    problems.append(f"{name} {version}: MIT without a copyright line; add a text to OVERRIDES")
                    continue
                holder = copyright_ if re.search(r"copyright|©|\(c\)", copyright_, re.I) \
                    else f"Copyright (c) {copyright_}"
                standard = standard.replace(MIT_COPYRIGHT_SLOT, holder)
            texts = [standard]
        if not texts:
            problems.append(f"{name} {version}: no license text for {expression}; add one to OVERRIDES")
            continue
        rows.append((name, version, expression, copyright_))
        body = "\n".join(dict.fromkeys(texts))
        key = hashlib.sha256(body.encode()).hexdigest()
        blocks.setdefault(key, {"body": body, "packages": []})["packages"].append(
            (name, version, expression, copyright_))

    polars = next((folder / POLARS_HTML for name, _, folder in packages
                   if name.startswith("Polars.NET.Native.") and (folder / POLARS_HTML).is_file()), None)
    if polars is None:
        problems.append(f"no Polars.NET.Native package with {POLARS_HTML} in this publish")
        crates = []
    else:
        page = polars.read_text(encoding="utf-8")
        crates = polars_crates(page)
        for crate, spdx_id in crates:
            exception = CRATE_EXCEPTIONS.get(crate.split(" ")[0])
            if spdx_id not in ALLOWED and exception != spdx_id:
                problems.append(f"Polars crate {crate}: license {spdx_id} is not on ALLOWED (Fedora's list)")
            spdx.add(spdx_id)

    if problems:
        print("third-party notices: " + str(len(problems)) + " problem(s)", file=sys.stderr)
        for p in problems:
            print("  " + p, file=sys.stderr)
        sys.exit(1)

    width = max(len(r[0]) for r in rows)
    vwidth = max(len(r[1]) for r in rows)
    desktop = any(r[0] == "Avalonia" for r in rows)
    lines = [
        "Third-party software in Acrux",
        "=============================",
        "",
        "Acrux is under the MIT license (LICENSE)"
        + ("; its logo and icon are under CC BY-ND 4.0\n(NOTICE-logo.md)." if desktop else "."),
        "It includes the software below, each under its own license; the license",
        "texts follow the list. The Rust crates inside Polars' native library",
        f"({len({c for c, _ in crates})}) are listed with their licenses in THIRD-PARTY-NOTICES-polars.html.",
        "",
    ]
    lines += [f"{n:<{width}}  {v:<{vwidth}}  {e}" for n, v, e, _ in rows]
    for block in sorted(blocks.values(), key=lambda b: b["packages"][0][0].lower()):
        lines += ["", "", "-" * 78]
        for n, v, e, c in block["packages"]:
            lines.append(f"{n} {v} ({e})" + (f", {c}" if c else ""))
        lines += ["-" * 78, "", block["body"].rstrip()]
    (publish / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (publish / "THIRD-PARTY-NOTICES-polars.html").write_bytes(polars.read_bytes())

    print(f"third-party notices: {len(rows)} packages, {len({c for c, _ in crates})} Polars crates, "
          f"{len(blocks)} license texts -> {publish}", file=sys.stderr)
    if "--spdx" in sys.argv:
        print(" AND ".join(sorted(spdx)))


if __name__ == "__main__":
    main()
