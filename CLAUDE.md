# FrameLink — Agent rules

Operational rules for any Claude Code agent working in this repository. These are **binding**, not suggestions. Read top to bottom before making changes.

---

## 0. Operating principles

These four principles govern every decision below. When a later rule and a principle disagree, the principle wins.

1. **Idempotency is non-negotiable.** Every command that mutates state must be safe to run a second time with no additional effect. This applies to commands you run on the Pi *and* to commands you write into the build guides. If you cannot make a command idempotent, explain why in prose and ask the user before committing it.
2. **Capture everything from the first run.** The user wipes the SD card before any session that needs fresh captures. That means every RUN/OUTPUT pair you produce is the *authoritative first-time output*. Record it as it happens — do not rely on reconstructing later.
3. **Read-only unless authorised.** Default to inspection commands. Every class of mutation (append to this file, install these packages, edit this config, reboot) requires the user's explicit go-ahead for that specific class in the current session. Authorisation does not generalise.
4. **Never fabricate.** If you did not capture an output, say so. Inferred or partial output is always labelled as such.

---

## 1. Capturing real command output from the Pi

Every RUN/OUTPUT pair in the build guides must contain **pixel-perfect** output captured from a real Pi. Never invent, paraphrase, or synthesise output text. If you cannot capture it, you must either skip the OUTPUT block or ask the user.

### 1.1 Target system

- Default host: the hostname the user set during guide 2. This repository uses `framelink-douwe.local` as the running example; substitute the hostname of whichever unit you are working on. The `.local` resolves via mDNS on the same LAN as the workstation.
- Default user: `framelink` (the username set during guide 2).
- Default shell: bash on Raspberry Pi OS Lite (Trixie / Debian 13).
- If the hostname does not resolve, ask the user for the correct hostname or IP before proceeding — do not guess, and do not run commands against a host you cannot confirm is the intended unit.

### 1.2 Password handling — non-negotiable

- Never store the password in any file, any committed artifact, any log, any bash history, any memory entry.
- The user supplies it in-session when needed. Treat each session as having an ephemeral password.
- If the user has not supplied a password and you need one to capture output, **ask for it**. Do not attempt to proceed, do not try key-based auth fallbacks, do not invent credentials.
- Pass the password to tools only via **environment variable**, inline with the command that uses it (e.g. `FL_PW='...' python -c '...'`). Never via a file.
- Never echo the password. Never include it in output summaries back to the user.
- Never add it to any password manager, keychain, SSH config, or autologin file.

### 1.3 Tooling available on the workstation

On the reference workstation — Windows 11, Git Bash, Python 3.12 — the following are already installed and are the only sanctioned tools for remote execution. The paths shown are that setup's stock install locations; substitute the equivalents if the workstation you are working from differs.

| Tool | Path / import | What it is good for |
| --- | --- | --- |
| `paramiko` 4.x | `python -c "import paramiko"` | Remote `exec_command` — captures *remote* stdout / stderr cleanly. Does NOT emit ssh-client-side messages. |
| `pywinpty` | `python -c "from winpty import PtyProcess"` | Spawns a real Windows ConPTY. Use this to drive the native `ssh.exe` when you need to capture **ssh-client-side** output (fingerprint prompt, `Connection to X closed by remote host.`, full interactive banner). |
| Native OpenSSH | `C:\Windows\System32\OpenSSH\ssh.exe` | The ssh client users will actually use. Only reachable non-interactively through `pywinpty`. |
| `ssh-keygen` | `C:\Windows\System32\OpenSSH\ssh-keygen.exe` | Use `-R <host>` to clear known_hosts entries when you need to recapture a first-connect flow. |
| `plink.exe` | `C:\Program Files\PuTTY\plink.exe` | Avoid. Its disconnect/banner messages differ from OpenSSH and silent failures are common (empty output when prompts are unmet). Use only when paramiko and pywinpty both cannot produce the needed output. |

Do not `pip install` new tooling into the system environment (Trixie enforces PEP 668 on the Pi side anyway). If you must install a package on the Windows side, use `pip install --user --quiet <pkg>` and record it in the write-log as a **persistent workstation mutation** — future agents inherit that install and your write-log entry is the only evidence it happened.

### 1.4 Which tool for which capture

| You need to capture | Use |
| --- | --- |
| Remote command stdout/stderr (apt, file edits, system inspection) | **paramiko** `exec_command(..., get_pty=False)` |
| Interactive MOTD / login banner as it reaches the terminal | **paramiko** `invoke_shell()` + short sleep + drain |
| SSH first-connect fingerprint prompt + answer flow | **pywinpty** spawning `ssh.exe`, script `yes` + password |
| `Connection to X closed by remote host.` after `sudo reboot` | **pywinpty** spawning `ssh.exe <host> sudo reboot`, read until EOF |
| Host-key-trusted reconnect banner with `Last login:` | **pywinpty** spawning `ssh.exe`, feed password |

### 1.5 Canonical patterns

**Paramiko remote exec (read-only safe default):**

```python
# FL_PW is injected by the shell caller as an env var
import os, paramiko
c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect('framelink-douwe.local', username='framelink',
          password=os.environ['FL_PW'],
          allow_agent=False, look_for_keys=False, timeout=15)
stdin, stdout, stderr = c.exec_command('uname -a', get_pty=False)
print(stdout.read().decode(), end='')
c.close()
```

Note: `allow_agent=False, look_for_keys=False` is required — without them paramiko tries the Windows SSH agent and fails with `key cannot be used for signing`.

**pywinpty + native ssh (for client-side output):**

```python
import os, time, re
from winpty import PtyProcess
p = PtyProcess.spawn([r'C:\Windows\System32\OpenSSH\ssh.exe',
                     'framelink@framelink-douwe.local'])
# read stream, match prompts ("yes/no", "password"), write responses.
# Strip ANSI with re.sub(r'\x1b\[[0-9;?]*[A-Za-z]', '', buf).
```

**Clear known_hosts to force a first-connect flow:**

```bash
"C:/Windows/System32/OpenSSH/ssh-keygen.exe" -R framelink-douwe.local
```

### 1.6 First-run capture: always start from a freshly flashed card

The ground rule is simple: **capture every command's output the first time it runs, from a Pi in a known-clean state.**

Because the user wipes the SD card before any capture session, there is no "the step was already applied" complication to work around. When you need to validate or update a build guide:

1. **Confirm state with the user.** Before you start, ask: "Is this a fresh flash, or do I need to record this from whatever state the Pi is in now?" If the user says fresh flash, you work from a blank slate and every RUN you issue produces pristine first-run OUTPUT suitable for the guide. If the user says this is not a fresh flash, do not attempt to revert-and-recapture — instead, note in your session report which steps cannot be validated without a wipe, and ask the user to schedule a wipe when they are ready.

2. **Record every SSH session chronologically.** Each command you run on the Pi, and the exact stdout/stderr it produced, goes into the write-log (§1.7) in the order it happened. The write-log *is* the source of truth for OUTPUT blocks — you do not reconstruct output from memory, you copy it from the log.

3. **Re-run any command whose output you did not capture cleanly.** Because everything is idempotent (§0), re-running is safe by construction. If the idempotency guards make the re-run show different output than the first run did (e.g. a `tee` echo only fires once), that is a signal that you already have the authoritative first-run output in an earlier log entry — use that.

4. **Never guess, never reconstruct from memory.** If you discover at the end of a session that a RUN has no captured OUTPUT, your only options are: (a) re-run it if idempotency guarantees identical output, (b) ask the user to wipe and re-run the session, or (c) mark the OUTPUT block as missing with a one-line note — never fill it in from inference.

### 1.7 Write-log discipline

A session-local write-log is the chronological record of every command you ran on the Pi (or on the workstation in a way that mutates shared state). Each entry records the command verbatim, the exact captured output, and the purpose in one sentence:

```
W<N> — <verbatim command> — captured: <short label>
       OUTPUT:
         <exact bytes>
```

The log is append-only inside a session. At end-of-session, classify each `W<N>` as:

- **keep** — belongs in the guide; its OUTPUT is what the guide will show.
- **discard** — diagnostic inspection that did not belong to any guide step; no further action needed.

Because principle §0.2 means every session starts from a fresh card, there is no "revert" or "reapply" category. If you find yourself wanting one, stop and re-read §1.6 — you are probably doing it wrong.

Do not commit commands to a guide unless they were captured in the write-log as **keep** and produced the OUTPUT text shown. If a command appears in the log but not in any guide, it should not exist — either add it to a guide or drop it from the session.

### 1.8 Read-only by default

Default to read commands (`cat`, `ls`, `grep`, `dmesg`, `systemctl status`, `lsmod`, `ls /sys/...`). Before running any write — `sudo sed -i`, `tee -a`, `apt install`, `apt full-upgrade`, `modprobe`, `systemctl enable`, `systemctl restart`, `reboot`, `raspi-config`, writes to `/boot/firmware/*`, writes under `~/.config/`, etc. — state the intent and wait for the user to authorise.

Authorisation is **narrow and session-scoped**:

- It applies to the specific command class you named. "OK to edit `/boot/firmware/config.txt` for the display overlay in guide 2 step 23" does *not* cover `apt install`, does *not* cover editing `cmdline.txt`, does *not* extend to the same file in a later guide. Each new class is a new ask.
- It expires when the session ends. A new session re-asks.
- It does not cascade. Running an authorised command that opens a downstream shell or script which would itself mutate state requires the downstream mutations to be named and authorised too.

When in doubt, ask. The cost of an extra question is trivial; the cost of an unauthorised mutation on a Pi that took time to set up is not.

### 1.9 Integrity / honesty

If you did not capture the OUTPUT from a live run, say so. Acceptable forms:

- Inferred from well-known client source text, e.g. the OpenSSH `Connection to X closed by remote host.` message.
- Derived from a different but equivalent capture (note it).
- Absent. Explicitly note the block is missing and why.

Never present inferred output as captured. On request, produce the honest inventory of what was captured vs inferred.

---

## 2. Markdown conventions for the build guides

The build guides live under `docs/` and are numbered `1-hardware-build-guide.md` through `15-local-fleet-manager.md`. Every image a guide references lives in a sibling folder of the same stem, e.g. `docs/2-sd-flash-first-boot/1.png`.

### 2.1 The seven-block step structure

Every step in a guide consists of **seven blocks**, in this exact order. There are no exceptions unless the user explicitly asks for one.

Guides 1 (hardware assembly) and 2 (SD flash & first boot) are exempt from the seven-block structure: their steps are physical/GUI actions verified by images, with guide 2's SSH tail using inline RUN/EXPECTED-OUTPUT pairs inside its numbered steps. All other software guides use the seven-block structure. Those two guides may each also carry **one** structural heading introducing their numbered list — `## Assembly` in guide 1, `## Steps` in guide 2 — because their steps are an ordinary numbered list rather than badge titles, and that single heading is the whole of the relaxation: every other rule in §2.6, the CHECKPOINT tail included, applies to them unchanged.

| # | Block | Badge emoji | Badge label (underscores in URL) | Badge colour (hex) |
| - | --- | :---: | --- | --- |
| 1 | PROBLEM | 🤔 | `PROBLEM` | `e05d44` |
| 2 | APPROACH | 💡 | `APPROACH` | `fbbf24` |
| 3 | TECHNICAL EXPLANATION | 🧠 | `TECHNICAL_EXPLANATION` | `8a2be2` |
| 4 | RUN THESE COMMANDS OVER SSH | 👤 | `RUN_THESE_COMMANDS_OVER_SSH` | `1e40af` |
| 5 | EXPECTED OUTPUT | 🍓 | `EXPECTED_OUTPUT` | `0d9488` |
| 6 | LOOK FOR | 🔎 | `LOOK_FOR` | `ea580c` |
| 7 | ACHIEVED | 🏆 | `ACHIEVED` | `228b22` |

All badges use `https://img.shields.io/badge/<emoji>-<LABEL>-<hex>?style=flat-square` and the `flat-square` style is fixed. Emojis, labels, and colours are pinned — do not substitute.

The block below uses an outer four-backtick ```` ```` `markdown`` fence only so this document can *display* the pattern verbatim. Your actual guide uses ordinary triple-backtick fences on the inner `bash` and `text` blocks; copy the inner content, not the outer wrapper.

````markdown
<a id="N-short-imperative-step-title"></a>
<img src="https://img.shields.io/badge/STEP_NN-Short_imperative_step_title-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step NN — Short imperative step title"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

<one- or two-sentence plain-language statement of the problem>

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

<one- or two-sentence plain-language summary of the solution direction>

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

<full technical explanation — what each piece does, why, how the command achieves it>

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
<one runnable command per line>
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
<pixel-perfect captured output>
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

<what to scan for in the output, what a successful line looks like, what an error would look like>

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

<what the reader has actually accomplished by running this step, in plain language>
````

The step-title badge is pinned: `for-the-badge` style, `height="50"`, left segment `STEP_NN` on `labelColor=228b22` (forest green, matching ACHIEVED / CHECKPOINT), right segment the title on `555555` (gray). `NN` is always two digits (`01`, `02`, ..., `12`). Shields.io URL encoding rules apply to the title: single `_` renders as a space, `__` renders as a literal underscore, `--` renders as a literal hyphen (so `Smoke-test` becomes `Smoke--test`). Characters shields.io cannot handle literally — parentheses, slashes, colons — must be percent-encoded (`%28`, `%29`, `%2F`, `%3A`). The `alt` attribute holds the human-readable `Step NN — Title` form and is what screen readers, GitHub's image-fallback text, and future search will see. The preceding `<a id="N-slug"></a>` anchor is the cross-reference target (see §2.7); the slug is the title lowercased with non-alphanumerics collapsed to single hyphens, matching GitHub's historical heading-slug convention.

Do **not** wrap the badge in an H1, H3, or any other heading. The badge is the step title. Apart from the single structural heading guides 1 and 2 are permitted above, there are no markdown headings between the guide-opening H1 (§2.6) and the end of the file.

- **No text outside the seven blocks.** Every sentence inside a step belongs to exactly one of the seven labelled blocks above. Do not add pre-step intros, mid-step asides, post-step wrap-ups, blockquotes, horizontal rules, or "note:" paragraphs between blocks or between steps. If something needs saying, put it in whichever of the seven blocks fits — PROBLEM / APPROACH for setup, TECHNICAL EXPLANATION for detail, LOOK FOR for verification, ACHIEVED for wrap-up. The only text allowed outside a step's seven blocks is the guide-level structure defined in §2.6 (H1 title, one-paragraph summary, CHECKPOINT) and the per-step anchor + badge-title pair defined above.
- **Nothing** goes between consecutive blocks inside a step — no prose, no blockquote, no image, no blank explanation. The blocks sit flush against each other separated only by the blank line the badge and its body need. Per-step reference images (§2.5) belong inside whichever block discusses the image.
- The PROBLEM, APPROACH, LOOK FOR, and ACHIEVED blocks must be written for readers with practically no computer experience. The TECHNICAL EXPLANATION block is where depth lives.

### 2.2 RUN THESE COMMANDS OVER SSH block rules

- **One command per line.** No backslash line continuations (`\` at end of line). Long pipelines stay on one logical line even if it is wide.
- **No inline shell comments (`#`)** inside the block. If a command needs explanation, put it in TECHNICAL EXPLANATION — ideally as a numbered list mapping each line to its purpose.
- Commands must be **idempotent** whenever they mutate system state. Use guard patterns:
  - Append-if-missing: `grep -qxF '<line>' <file> || echo '<line>' | sudo tee -a <file>`
  - Edit-if-missing: `grep -q '<pattern>' <file> || sudo sed -i 's|<old>|<new>|' <file>`
  - File-overwrite: `cat > <path> << 'EOF' ... EOF` (this is already idempotent in effect).
- Do not use `sudo apt install ...` without `-y`. Do not use `apt-get upgrade`; always `apt full-upgrade`.
- The code fence uses the `bash` language tag.

### 2.3 EXPECTED OUTPUT block rules

- **Pixel-perfect captured text.** See §1.
- Garble privacy-sensitive fields in-place:
  - Fingerprints → `SHA256:xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`
  - Real IPv6 addresses → `fe80::xxxx:xxxx:xxxx:xxxx%N` (or `%eth0` if the real interface is shown)
  - MAC addresses → `xx:xx:xx:xx:xx:xx`
  - Serial numbers → `xxxxxxxx`
  - Real WiFi SSIDs, router IPs, home-network names → `<redacted>` or generic
  - Usernames chosen by the user (`framelink`, `framelink-douwe`) stay — they are part of the public example.
  - Never include a password, API key, JWT, Raspberry Pi Connect token, or LiveKit secret.
- If the captured output is very long, truncate with `...` between real lines rather than summarising. Note the truncation in the LOOK FOR block.
- If the output differs between first-run and subsequent-run (e.g. because of idempotent guards or apt state), show one case in the EXPECTED OUTPUT block and describe the other case in LOOK FOR.
- The code fence uses the `text` language tag.

### 2.4 Content placement inside the seven blocks

- **PROBLEM** — what is wrong or missing right now, phrased for a non-technical reader. One or two sentences.
- **APPROACH** — the plain-language fix direction. One or two sentences. No jargon.
- **TECHNICAL EXPLANATION** — the deep "why and how", including line-by-line mapping of the RUN block when it helps.
- **RUN THESE COMMANDS OVER SSH** — just the commands (see §2.2).
- **EXPECTED OUTPUT** — just the captured text (see §2.3).
- **LOOK FOR** — what the reader scans the output for, what a pass looks like, what a fail would look like. Also the home for "if the output differs" and truncation notes.
- **ACHIEVED** — one- or two-sentence plain-language statement of what the reader has now accomplished, and explicitly what they have *not* yet accomplished if relevant.

No stranded text anywhere inside a step. None.

### 2.5 Step numbering and images

- Top-level steps are numbered `01`, `02`, `03`, ... (always two digits, embedded in the step-title badge's left segment — see §2.1). Every step contains exactly the seven blocks defined in §2.1, with no stray prose between them (see §2.1's "no text outside the seven blocks" rule).
- Each step's title is a single shields.io badge (no heading wrapper, no ordered-list marker). The badge is preceded by an HTML anchor `<a id="N-slug"></a>` that serves as the cross-reference target in place of a markdown heading anchor (see §2.7). `N` in the anchor is the unpadded step number (`1`, `2`, ..., `12`); `NN` inside the badge URL is the zero-padded form.
- Per-step reference images (e.g. Imager screenshots) are rendered inline as markdown images pointing at `<guide-stem>/<filename>.<ext>`, placed inside whichever of the seven blocks discusses the image. The folder `<guide-stem>` is the filename of the guide without the `.md` extension.
- All external images referenced by a guide must be downloaded into the guide's per-guide folder — no hot-linked remote URLs. The badge URLs (`img.shields.io`) are the only permitted external image references.

### 2.6 Guide-level structure

Every guide begins with an H1 title, followed by a one-paragraph summary, and a horizontal rule. The numbered steps follow directly after the horizontal rule — do not add a `## Steps` heading. The guide-opening H1 is the **only** markdown heading in the file; step titles are badges, not headings (see §2.1 and §2.5). Guides 1 and 2 are the sole exception on both counts, under the structural-heading allowance in §2.1. The guide-title H1 form differs between the hardware guide and the software guides:

- **Guide 1** (hardware assembly) uses the form `# FrameLink Hardware Build Guide`.
- **Guides 2 and later** (software) use the form `# Software Build Guide NN — <Title>` where `NN` is the two-digit sequence number matching the filename prefix (e.g. `02`, `03`, ...).

Every guide ends with a **CHECKPOINT** section: a horizontal rule, a single `<br>` tag for vertical breathing room, the checkpoint badge, then one or more sentences stating the observable condition(s) that prove the guide succeeded. The badge is pinned:

```markdown
---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

<one or more sentences describing the acceptance criterion>
```

The checkpoint badge uses the louder `for-the-badge` style (not `flat-square`) so it reads as the end-of-guide milestone, and shares the forest-green `228b22` of the ACHIEVED block. The emoji (🚩), label, colour, and style are fixed. Do not centre, resize, or otherwise restyle it.

No "Next Steps" sections, no cross-guide navigation blocks, no consolidated planning document outside the per-guide structure.

### 2.7 Referencing other guides

Use relative markdown links, e.g. `[guide 5 step 3](5-kiosk-base.md#3-enable-console-autologin)`. The fragment must match a `<a id="...">` anchor that actually exists in the target file — since step titles are badges (§2.1) and generate no automatic heading anchors, the anchors you link to are only the ones explicitly written above each step badge. Never hard-code an anchor ID you have not verified by opening the target file.

### 2.8 Link style — always human-readable

Every link in prose must be a markdown link of the form `[visible label](url)`. Never paste a bare URL into prose.

- **Yes:** `See the [Waveshare 10.1-DSI-TOUCH-A wiki](https://www.waveshare.com/wiki/10.1-DSI-TOUCH-A) for panel details.`
- **No:** `See https://www.waveshare.com/wiki/10.1-DSI-TOUCH-A for panel details.`

Rules for the visible label:

- Use the human name of the destination (site name, page title, product name, section title). Never use "here", "this link", or "click here".
- Do not duplicate the URL in both the label and the parentheses. If the most natural label is the domain itself, use a short form: `[id.raspberrypi.com](https://id.raspberrypi.com/)` rather than repeating the scheme in the label.
- When the link points inside this repo, use a relative path, not an absolute `https://github.com/...` URL.

Bare URLs are allowed *only* inside code fences (`bash`, `text`, `ini`, `yaml`, etc.) where they represent literal strings — e.g. a URL being passed to `chromium --kiosk`, or a `git remote add` target. Everywhere else in prose they must be wrapped.

### 2.9 Don't re-explain recurring patterns

Some patterns appear in nearly every guide: idempotent `grep ... || ... | sudo tee -a` appends echoing on first run but not on subsequent runs, `sudo sed -i` rewriting files silently, `sudo reboot` producing an SSH client-side disconnect message whose wording varies by client. A reader working through the guides in order will have internalised these after the first encounter — re-explaining them in every subsequent guide is noise.

Rules:

- **Never** explain first-run vs. subsequent-run output differences for the standard idempotent append/edit patterns (`grep ... || ... tee -a`, `grep ... || sed -i`) after the first time they appear in a guide that readers will read before the one you are writing. Just show the first-run EXPECTED OUTPUT as captured and move on.
- **Never** link or cross-reference back to the "SSH client wording differs" discussion (currently in [guide 2 step 21](docs/2-sd-flash-first-boot.md#steps)) whenever a `sudo reboot` appears in a later guide. The reader has seen it. The disconnect line in EXPECTED OUTPUT stands on its own.
- If a guide does something genuinely new with idempotency or with reboot behaviour — a first-run-only side-effect that matters, a reboot that takes unusually long, a command whose subsequent-run output is *materially* different from first-run — then narrate that specifically. The rule above is about suppressing the *rote* re-explanation, not suppressing real novelty.

---

## 3. Repository conventions

- `docs/<guide>.md` ↔ `docs/<guide>/` for per-guide image assets.
- `research/` holds pre-decision exploration. It is historical reference; do not rewrite it unless a decision changes.
- `README.md` is the project index. It points at every build guide currently in `docs/` and holds the bill of materials. Update it whenever a guide is renamed, added, or removed.
- Do not create new markdown files outside `docs/` and `research/` unless explicitly asked.
- Never edit `CLAUDE.md` (this file) to weaken rules. Agents may propose strengthening them to the user.
