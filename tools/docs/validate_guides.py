"""Structural validator for docs/*.md against CLAUDE.md section 2.

Checks, per guide:
  1. Exactly one markdown heading in the file, and it is the H1 (2.1, 2.6).
  2. H1 form: guide 1 'FrameLink Hardware Build Guide'; 2+ 'Software Build Guide NN - <Title>'.
  3. The seven block badges appear in the pinned order, with pinned emoji/label/colour/style (2.1).
  4. Every step is <a id=...> immediately followed by the pinned step-title badge (2.1, 2.5).
  5. Step numbers run 01..N, anchors carry the unpadded number, slug matches the badge title.
  6. Nothing between consecutive blocks of a step other than the block body (checked as:
     the badge sequence inside a step is exactly the seven, in order).
  7. CHECKPOINT tail: '---', '<br>', pinned checkpoint badge, then prose (2.6).
  8. No bare URLs in prose outside code fences (2.8).
  9. Every relative markdown link resolves to a file that exists, and every #fragment
     matches an <a id="..."> that exists in the target file (2.7).
"""

import re
import sys
from pathlib import Path

DOCS = Path(sys.argv[1] if len(sys.argv) > 1 else "docs")

SEVEN = [
    ("PROBLEM", "\U0001f914", "PROBLEM", "e05d44"),
    ("APPROACH", "\U0001f4a1", "APPROACH", "fbbf24"),
    ("TECHNICAL EXPLANATION", "\U0001f9e0", "TECHNICAL_EXPLANATION", "8a2be2"),
    ("RUN THESE COMMANDS OVER SSH", "\U0001f464", "RUN_THESE_COMMANDS_OVER_SSH", "1e40af"),
    ("EXPECTED OUTPUT", "\U0001f353", "EXPECTED_OUTPUT", "0d9488"),
    ("LOOK FOR", "\U0001f50e", "LOOK_FOR", "ea580c"),
    ("ACHIEVED", "\U0001f3c6", "ACHIEVED", "228b22"),
]

BLOCK_RE = re.compile(
    r"^!\[(?P<alt>[^\]]+)\]\(https://img\.shields\.io/badge/"
    r"(?P<emoji>[^-]+)-(?P<label>[A-Z_]+)-(?P<colour>[0-9a-f]{6})\?style=(?P<style>[a-z-]+)\)$"
)
STEP_RE = re.compile(
    r'^<img src="https://img\.shields\.io/badge/STEP_(?P<nn>\d{2})-(?P<title>[^-]*(?:-[^-]*)*?)'
    r'-555555\?style=for-the-badge&labelColor=228b22" height="50" alt="(?P<alt>[^"]+)"/>$'
)
ANCHOR_RE = re.compile(r'^<a id="(?P<id>[^"]+)"></a>$')
CHECKPOINT = (
    "![CHECKPOINT](https://img.shields.io/badge/\U0001f6a9-CHECKPOINT-228b22?style=for-the-badge)"
)
HEADING_RE = re.compile(r"^#{1,6} ")
LINK_RE = re.compile(r"\[(?P<label>[^\]]*)\]\((?P<url>[^)\s]+)\)")
BARE_URL_RE = re.compile(r"(?<![(\"'\w=])https?://[^\s)\"'<>]+")

problems = []
anchors_by_file = {}


def slug(title_segment):
    # Shields URL title -> human title: '--' is a literal hyphen, '__' a literal underscore,
    # single '_' a space; then percent-decoding for the few encoded characters used.
    text = title_segment.replace("--", "\x00").replace("__", "\x01")
    text = text.replace("_", " ").replace("\x01", "_").replace("\x00", "-")
    for enc, dec in (("%28", "("), ("%29", ")"), ("%2F", "/"), ("%3A", ":"), ("%2C", ",")):
        text = text.replace(enc, dec)
    return text


def slugify(text):
    return re.sub(r"-+", "-", re.sub(r"[^a-z0-9]+", "-", text.lower())).strip("-")


def code_fence_mask(lines):
    mask, inside = [], False
    for line in lines:
        # Fences inside a numbered list are indented (guides 1-2), so match after stripping.
        if line.lstrip().startswith("```"):
            inside = not inside
            mask.append(True)
        else:
            mask.append(inside)
    return mask


for path in sorted(DOCS.glob("*.md")):
    text = path.read_text(encoding="utf-8")
    lines = text.split("\n")
    anchors_by_file[path.name] = set(ANCHOR_RE.match(l).group("id") for l in lines if ANCHOR_RE.match(l))

for path in sorted(DOCS.glob("*.md")):
    name = path.name
    text = path.read_text(encoding="utf-8")
    lines = text.split("\n")
    fenced = code_fence_mask(lines)

    def fail(msg, n=None):
        problems.append(f"{name}{'' if n is None else f':{n}'} — {msg}")

    headings = [(i + 1, l) for i, l in enumerate(lines) if HEADING_RE.match(l) and not fenced[i]]
    # CLAUDE.md section 2.1 permits guides 1 and 2 exactly one structural heading each, because
    # their steps are an ordinary numbered list rather than badge titles. This validator predates
    # that allowance and reported both files as failures for a week; two permanent false failures
    # are worse than none, because a check nobody can ever get to zero is a check people stop
    # reading. The allowance is narrow on purpose: the file, the level and the text are all pinned,
    # so a third heading, a different one, or the same one in another guide still fails.
    allowed_extra = {
        "1-hardware-build-guide.md": "## Assembly",
        "2-sd-flash-first-boot.md": "## Steps",
    }.get(name)
    if allowed_extra and len(headings) == 2 and headings[1][1].strip() == allowed_extra:
        headings = headings[:1]
    if len(headings) != 1:
        fail(f"expected exactly one heading (the H1); found {len(headings)}: "
             + "; ".join(f"line {n} {l!r}" for n, l in headings))
    elif not headings[0][1].startswith("# "):
        fail(f"the only heading is not an H1: {headings[0][1]!r}", headings[0][0])
    else:
        h1 = headings[0][1][2:]
        if name.startswith("1-"):
            if h1 != "FrameLink Hardware Build Guide":
                fail(f"guide 1 H1 form: {h1!r}")
        else:
            nn = name.split("-")[0].zfill(2)
            if not re.match(rf"^Software Build Guide {nn} — .+", h1):
                fail(f"H1 must read 'Software Build Guide {nn} — <Title>'; found {h1!r}")

    # Steps: anchor line followed by the step badge.
    steps = []
    for i, line in enumerate(lines):
        m = STEP_RE.match(line)
        if not m:
            if "img.shields.io/badge/STEP_" in line and not fenced[i]:
                fail(f"step badge does not match the pinned form: {line!r}", i + 1)
            continue
        anchor = ANCHOR_RE.match(lines[i - 1]) if i > 0 else None
        if not anchor:
            fail("step badge is not immediately preceded by its <a id=...> anchor", i + 1)
            continue
        steps.append((i, int(m.group("nn")), m.group("title"), m.group("alt"), anchor.group("id")))

    guide1or2 = name.startswith(("1-", "2-"))

    for index, (line_no, nn, title, alt, anchor_id) in enumerate(steps, start=1):
        if nn != index:
            fail(f"step badge numbered {nn:02d} at position {index}", line_no + 1)
        human = slug(title)
        if alt != f"Step {nn:02d} — {human}":
            fail(f"alt text {alt!r} does not match 'Step {nn:02d} — {human}'", line_no + 1)
        want = f"{nn}-{slugify(human)}"
        if anchor_id != want:
            fail(f"anchor {anchor_id!r} should be {want!r}", line_no)

    checkpoint_line = lines.index(CHECKPOINT) if CHECKPOINT in lines else len(lines)

    if not guide1or2 and steps:
        bounds = [s[0] for s in steps] + [checkpoint_line]
        for index in range(len(steps)):
            seg = lines[bounds[index]: bounds[index + 1]]
            seg_fenced = code_fence_mask(seg)
            found = []
            for j, line in enumerate(seg):
                m = BLOCK_RE.match(line)
                if m and not seg_fenced[j]:
                    found.append(m)
                elif "img.shields.io/badge/" in line and not seg_fenced[j] and not STEP_RE.match(line) \
                        and CHECKPOINT not in line and not line.startswith("<a id="):
                    fail(f"unrecognised badge in step {steps[index][1]:02d}: {line!r}",
                         bounds[index] + j + 1)
            if len(found) != 7:
                fail(f"step {steps[index][1]:02d} has {len(found)} of the seven blocks: "
                     + ", ".join(m.group("label") for m in found))
                continue
            for m, (alt_want, emoji, label, colour) in zip(found, SEVEN):
                if m.group("alt") != alt_want or m.group("emoji") != emoji \
                        or m.group("label") != label or m.group("colour") != colour \
                        or m.group("style") != "flat-square":
                    fail(f"step {steps[index][1]:02d} block badge is not the pinned "
                         f"{alt_want}: {m.group(0)!r}")

    tail = [l for l in lines[-8:] if l.strip()]
    if CHECKPOINT not in text:
        fail("no CHECKPOINT badge")
    else:
        c = lines.index(CHECKPOINT)
        before = [l for l in lines[:c] if l.strip()][-2:]
        if before != ["---", "<br>"]:
            fail(f"CHECKPOINT is not preceded by '---' then '<br>'; found {before}")
        if not [l for l in lines[c + 1:] if l.strip()]:
            fail("CHECKPOINT has no acceptance sentence after it")

    for i, line in enumerate(lines):
        if fenced[i] or line.startswith("<img src=") or line.startswith("<a id="):
            continue
        stripped = LINK_RE.sub(lambda m: " " * len(m.group(0)), line)
        stripped = re.sub(r"<[^>]+>", "", stripped)
        # Inline code spans are literal strings, same allowance as a fenced block (2.8).
        stripped = re.sub(r"`[^`]*`", "", stripped)
        for m in BARE_URL_RE.finditer(stripped):
            fail(f"bare URL in prose: {m.group(0)}", i + 1)

    for i, line in enumerate(lines):
        if fenced[i]:
            continue
        for m in LINK_RE.finditer(line):
            url = m.group("url")
            if url.startswith(("http://", "https://", "mailto:")):
                continue
            target, _, frag = url.partition("#")
            resolved = (path.parent / target).resolve() if target else path.resolve()
            if not resolved.exists():
                fail(f"link target does not exist: {url}", i + 1)
                continue
            if frag:
                known = anchors_by_file.get(resolved.name)
                if known is None:
                    body = resolved.read_text(encoding="utf-8")
                    known = set(re.findall(r'<a id="([^"]+)"></a>', body))
                    known |= {slugify(h[2:]) for h in body.split("\n") if h.startswith("# ")}
                if frag not in known:
                    fail(f"fragment #{frag} not found in {resolved.name}", i + 1)

print(f"checked {len(list(DOCS.glob('*.md')))} guides")
for p in problems:
    print("  FAIL " + p)
print(f"{len(problems)} problem(s)")
sys.exit(1 if problems else 0)
