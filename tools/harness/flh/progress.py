"""The resumable progress file - version2.md section 5.5.

    "Long runs lose context, so progress is written to disk continuously and any session
     can resume mid-milestone."

This module owns ``tools/harness/progress.json``. That file is deliberately **not**
gitignored (``.gitignore`` covers ``tools/harness/runs/`` only): it is the record a fresh
session with no memory of anything reads first to learn exactly where the build stands.

Design rules the shape follows
------------------------------
1. **Machine-readable first, human-readable anyway.** Plain JSON, two-space indent, one
   fact per key, and a canonical key order rewritten on every save, so a git diff shows what
   actually changed rather than where a key happened to be appended.
2. **States are earned, never asserted.** A capability becomes ``proven`` only when the
   command that proves it actually ran and succeeded; the harness writes that transition,
   nobody edits it in by hand. ``provenBy`` names the command and ``provenUtc`` the moment.
3. **Continuous, not end-of-run.** Every subcommand writes an ``inFlight`` marker *before*
   doing the work and clears it after. A file still holding an ``inFlight`` marker is the
   signature of a session that died mid-command, which is exactly what the resuming
   session needs to know and cannot otherwise discover.
4. **Atomic.** Written to a temp file in the same directory and ``os.replace``d, so a kill
   between two writes can never leave truncated JSON - the one failure that would make the
   resume mechanism worse than useless.
5. **Blockers are first-class.** Anything the harness cannot do, and what it would need in
   order to do it, is listed rather than implied by an absence.

What rule 2 could not carry, and what was done about it
-------------------------------------------------------
Rule 2 was written when M0 was the whole world, and M0 is the one milestone every part of
which some ``fl.py`` subcommand can perform. M1, M2 and M2.5 are not like that. A frame
appearing in the adoption queue, an operator pressing **Adopt**, nine resources converging
and staying converged across a real reboot, a console stage legible on a physical panel -
no command in this harness can perform any of those, so under rule 2 alone they can never
be recorded at all, and the file's answer to "where does the build stand" stays frozen at
the last thing the harness itself happened to do. That is exactly how this file came to
report M0 as the frontier while three later milestones were already built.

So there are now **two** ways a state gets here, and they are labelled rather than blended:

* ``proven`` - earned by a harness command. Only :func:`prove` writes it, from the code path
  that ran the command. Unchanged, and still the only word that means "this harness did it".
* ``witnessed`` - observed on real hardware by a person, and recorded **in this module** with
  its provenance: what was seen, when, on which host, where the durable evidence lives, and
  what the observation does *not* cover. It lives in code and not in the JSON because the JSON
  is a generated artifact - :func:`save` rewrites the derived sections on every write, so a
  hand-edit there survives until the next ``fl.py`` invocation and no longer. In ``_MILESTONES``
  it is a reviewable diff with the evidence attached, which is the thing a bare assertion in a
  data file can never be.

A third kind is derived rather than recorded at all: the M3 ledger below counts what the
repository itself says, on every save, from the two files that are authoritative for those
numbers. A count that is measured cannot go stale, and going stale is the failure this whole
module exists to prevent.

Why the schema identifier did not move
--------------------------------------
This revision changes the shape substantially - a milestone ladder instead of one milestone,
a resource ledger, an orientation block - and still leaves ``SCHEMA`` alone. That is
deliberate. ``load`` treats an unrecognised schema as a file to step around, and a longer-
running subcommand holds *its* copy of this module in memory for the whole run: the shape was
revised while a ``fl.py build`` was thirty minutes into an emulated container build. Bumping
the identifier would have made that already-running process treat the new file as foreign at
its ``finally``, which is the one moment the resume mechanism must not be the thing that
loses the record. The keys added here are additive, an older writer preserves every one of
them, and the two fields it does overwrite (``readMeFirst``, and the legacy singular
``milestone`` it re-creates) are repaired by :func:`_refresh` on the next save. ``load`` now
migrates rather than blanks, so the next bump is free.
"""

from __future__ import annotations

import json
import os
import re
from contextlib import contextmanager
from datetime import UTC, datetime
from typing import Any

from .config import HA_ENTITY, HA_URL, MULE_HOST, MULE_USER, PROGRESS_FILE, REPO_ROOT

SCHEMA = "framelink.harness.progress/1"

#: Bound on the event log. Enough to see a whole working session, small enough that the
#: file stays readable and diffable.
LOG_LIMIT = 200

#: The dev Fleet Manager the mule is pointed at. Not in ``config.py`` because no harness
#: subcommand talks to it - it is here because a resuming session cannot find it anywhere
#: else, and the mule's journal is otherwise the only record that this address is the one.
CONTROL_URL = "http://10.20.30.200:5199"

_READ_ME_FIRST = [
    "This is the FrameLink v2 progress file. It is written continuously by "
    "tools/harness/fl.py and is the authoritative answer to 'where does the build stand' "
    "for a session that remembers nothing. Read it top to bottom: nextActions and blockers "
    "are what to do, milestones is where the build is, capabilities is what this harness "
    "has been proven able to do, resources is the M3 ledger, findings holds the measured "
    "answers to questions the catalog was carrying open, orientation names the other "
    "durable artifacts, environment names the hosts and the credentials.",
    "Run `python tools/harness/fl.py status` first. It re-probes this session live (docker, "
    "dotnet, paramiko, the mule on tcp/22, which credentials are present) and rewrites "
    "blockers and nextActions from what it finds, so a stale obstacle cannot survive the "
    "one command a resuming session is told to run.",
    "Two words mean different things and are never blended. 'proven' is earned: some fl.py "
    "subcommand ran, succeeded, and wrote that transition itself - see provenBy and "
    "provenUtc. 'witnessed' is observed on real hardware by a person and recorded in "
    "tools/harness/flh/progress.py with its evidence, because no command in this harness "
    "can perform an adoption or read a physical panel. Nothing here is ever true because "
    "somebody typed it into the JSON: save() rewrites every derived section, so a hand-edit "
    "lasts until the next fl.py invocation and no longer.",
    "To record a new hardware observation, add it to _MILESTONES, "
    "_HARDWARE_VERIFIED_RESOURCES or _HARDWARE_FINDINGS in tools/harness/flh/progress.py "
    "with what was seen, when, on which host and where the evidence lives. Use "
    "_HARDWARE_FINDINGS when the reading settles a question rather than converging a "
    "resource, and say what it rules out. That is a reviewable commit, which is the "
    "point. Do not write it into this file.",
    "If inFlight is not null a previous session died in the middle of that command and "
    "whatever it was changing is unverified - which matters most for `deploy` and `power`, "
    "the two subcommands that change something outside this repository.",
    "Credentials are supplied inline per session and never stored, logged or defaulted "
    "(CLAUDE.md section 1.2). The environment block names which variable unlocks what; it "
    "holds no values and must never be made to.",
]

#: The six pieces M0 is made of, plus the acceptance condition that binds them.
#: version2.md section 5.1: "A code change reaches the mule and is verified with no human help:
#: build path, deploy script, power-cycle control, screenshot + journal collection,
#: resumable progress file, test runner."
_CAPABILITIES: list[tuple[str, str, str]] = [
    (
        "build-path",
        "Native AOT linux-arm64 binary built in an emulated container",
        "fl.py build",
    ),
    (
        "test-runner",
        "Test suite runs and propagates its exit code",
        "fl.py test",
    ),
    (
        "progress-file",
        "Progress is written to disk continuously and survives a dead session",
        "any fl.py subcommand",
    ),
    (
        "deploy",
        "Binary and systemd unit reach the mule idempotently and the service restarts",
        "fl.py deploy",
    ),
    (
        "power-cycle",
        "Smart plug switches under harness control with wrong-entity and wear guards",
        "fl.py power",
    ),
    (
        "collect",
        "Screenshot and journal tail come back from the mule",
        "fl.py collect",
    ),
    (
        "closed-loop",
        "A code change reaches the mule and is verified with no human help (M0 done)",
        "fl.py build && fl.py deploy && fl.py collect",
    ),
]

#: version2.md section 5.1's ladder, verbatim in ``doneWhen`` and in order. ``stateFrom``
#: says how each row's state is arrived at and is the whole honesty mechanism:
#:
#:   capabilities  derived from the seven M0 capabilities below. Earned, never written here.
#:   resources     derived from the repository on every save. Measured, never written here.
#:   witness       observed on hardware and recorded here with its evidence, because no
#:                 command in this harness could ever perform it.
#:   none          nothing has happened yet.
#:
#: A ``witness`` row carries ``evidence`` with five fields, and the fifth is the one that
#: makes it worth trusting: ``notWitnessed`` states what the observation did *not* cover, so
#: the record cannot quietly grow into a bigger claim than the thing somebody actually saw.
_MILESTONES: list[dict[str, Any]] = [
    {
        "id": "M0",
        "title": "Autonomy harness",
        "doneWhen": (
            "A code change reaches the mule and is verified with no human help: build path, "
            "deploy script, power-cycle control, screenshot + journal collection, resumable "
            "progress file, test runner."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "capabilities",
    },
    {
        "id": "M1",
        "title": "Walking skeleton",
        "doneWhen": (
            "Agent connects -> appears pending -> adopted in the GUI -> reconciles one trivial "
            "resource -> self-updates from the Fleet Manager. Every integration risk retired at "
            "once."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "witness",
        "state": "done",
        "evidence": {
            "how": "observed-on-hardware",
            "what": (
                "The agent on the mule connected to the dev Fleet Manager, appeared in the "
                "adoption queue as a pending device, was adopted from the GUI under the name "
                "'Mule' (device T1RJ-6JCQ-9HN8-3920, hardware serial 19aa037e525b27b6), went "
                "online, and streamed live reconcile telemetry that the GUI rendered."
            ),
            "whenUtc": "2026-08-15",
            "where": (
                f"mule {MULE_USER}@{MULE_HOST}, Fleet Manager running on the workstation at "
                f"{CONTROL_URL}"
            ),
            "recordedIn": (
                "git log (commits 7e41cb2 control console, 43dfc51 protocol, 68ffbdf GUI); the "
                "device identity is visible in every journal capture under tools/harness/runs/ "
                "(gitignored, local only); the adoption row itself lives in the Fleet Manager's "
                "SQLite database outside this repository"
            ),
            "notWitnessed": (
                "The self-update leg was not separately re-observed in that session. The update "
                "path is wired and running - the mule's journal shows repeated update checks "
                "against /agent/release/linux-arm64 - but they are failing right now because the "
                "dev Fleet Manager is not up, so a successful binary swap has not been watched."
            ),
        },
    },
    {
        "id": "M2",
        "title": "Reconciler engine",
        "doneWhen": (
            "DAG, status vocabulary, retry/backoff, reboot-verified apply, escalation ladder, "
            "live telemetry, console and browser narration."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "witness",
        "state": "done",
        "evidence": {
            "how": "observed-on-hardware",
            "what": (
                "Three separate observations, all on the mule. (1) 2026-08-15: nine resources "
                "converged and each was verified after a real reboot, not after a service "
                "restart, and the console stage rendered on the physical DSI panel and read "
                "correctly. (2) 2026-08-16: the console stage's own terminal and the handover "
                "both ways - fl-agent holds /dev/tty8 while agetty keeps /dev/tty1, "
                "/sys/class/tty/tty0/active reads tty8 with no compositor up, getty@tty1 is "
                "still active so 5.5's physical login survives, and the EIO line 874823b removed "
                "does not appear on this boot; with a compositor up the log carries 'The screen "
                "is now the product's - tty1 is in front'. PASS on 874823b's own criteria. "
                "(3) 2026-08-16: 2.5 rung 3's operator retry used for the first time in "
                "production, over POST /api/devices/{id}/retry/{resource} - six presses, all "
                "HTTP 200 'sent', each one reaching the frame's journal as 'the attempt budget "
                "was reset by the Fleet Manager'. boot.autologin.getty-tty1 left Escalated and "
                "its twelve dependents left Blocked; session.bash-profile-exec-labwc went "
                "straight to InSync from the retry alone with no reboot, which is what a "
                "resource that was never actually broken looks like when the ladder lets go of "
                "it. The frame reached 72 of 79 in-sync."
            ),
            "whenUtc": "2026-08-16",
            "where": f"mule {MULE_USER}@{MULE_HOST}, physical panel on card0-DSI-2",
            "recordedIn": (
                "git log (011cf3a reconciler engine, 28a5264 agent seams and honest console, "
                "9eedf91 telemetry payloads, 6d77144 console stage, 874823b console stage on "
                "tty8, 97862c6 and a95958b the two halves of retry, fe00f40 the retry button and "
                "its bundle, 74cdedf the session-readiness gate); the panel was photographed by "
                "`fl.py collect` - the tty8 observation's screenshot is "
                "runs/20260816T021133Z-collect/screenshot.png, which shows the console stage "
                "painting the catalog on the physical panel"
            ),
            "notWitnessed": (
                "The retry presses were made through the API, not through the GUI button "
                "committed in fe00f40 - the button, its client method and its rendered bundle "
                "are covered by GuiFreshnessTests but no operator has clicked one on a screen. "
                "The 409 'offline' branch was never exercised against a real disconnected frame; "
                "every press landed on a live socket. Reboot-verification is claimed for 36 "
                "resources and not for all 72 that are in-sync - see the comment on "
                "_HARDWARE_VERIFIED_RESOURCES for why that is an evidence limit. "
                "ONE OBSERVATION FROM THAT SESSION IS STILL UNEXPLAINED, and is written down "
                "here rather than guessed at: on 2026-08-16 at 07:16:18 CEST, "
                "unit.chromium-kiosk.running-matches-content read a command line for a browser "
                "that was genuinely running as pid 1253 and reported ALL twelve compared "
                "arguments missing - 'running without --ozone-platform=wayland, "
                "--user-data-dir=/tmp/framelink-chromium, --kiosk, ...' - from a process whose "
                "/proc/1253/cmdline had been measured minutes earlier as carrying every one of "
                "them. The obvious cause was checked and excluded: CommandLineOf splits on NUL "
                "correctly. 7b1e5f7 explains the two attempts that followed, where MainPID is 0 "
                "during ExecStartPre and the resource says 'no browser process is running', but "
                "it does not explain this one, which found a non-empty command line and compared "
                "it wrongly. What would settle it is logging the argv the resource actually read "
                "at the moment it decides. Until then it is undetermined, not fixed."
            ),
        },
    },
    {
        "id": "M2.5",
        "title": "Image generation",
        "doneWhen": (
            "A card flashed from a Fleet-Manager-generated image boots, starts the agent "
            "unattended, and appears in the adoption queue (section 3.9)."
        ),
        "specRef": "version2.md section 5.1 and section 3.9",
        "stateFrom": "witness",
        "state": "in-progress",
        "evidence": {
            "how": "measured-against-the-real-base-image",
            "what": (
                "The generator is built (src/FrameLink.Control/Imaging) and its whole premise was "
                "measured against the real pinned 2026-06-18-raspios-trixie-arm64-lite.img in a "
                "plain debian:trixie-slim container with no --privileged, no --cap-add and no "
                "device mapping: debugfs writes the binary and sets mode and owner, debugfs "
                "symlink does what `systemctl enable` does, mcopy writes the boot-partition file, "
                "and `e2fsck -fn` calls the result clean. `mount -o loop` fails in that same "
                "container, which is what proves loopback was never involved."
            ),
            "whenUtc": "2026-08-15",
            "where": "workstation, debian:trixie-slim container",
            "recordedIn": (
                "git log commit ba5a873 carries the full measurement and the debugfs-exits-0-on-"
                "failure finding; decision 52 supersedes 32 in version2.md Appendix A; the tests "
                "are tests/FrameLink.Tests/ControlImageGenerationTests.cs"
            ),
            "notWitnessed": (
                "The milestone's own acceptance test - flash a card, watch a row appear - has not "
                "happened and cannot yet: version2.md section 5.3 item 3 records that no SD card "
                "reader is attached. Nothing generated has been written to a card, booted, or seen "
                "in the adoption queue. This milestone is NOT done."
            ),
        },
    },
    {
        "id": "M3",
        "title": "Resource migration",
        "doneWhen": (
            "Guide by guide, lowest-risk first, firmware DFU last. Each group passes the triple "
            "bar: state-diff vs the frozen v1 reference, checkpoint assertions, validation battery "
            "on the mule."
        ),
        "specRef": "version2.md section 5.1",
        "stateFrom": "resources",
    },
    {
        "id": "Mn+1",
        "title": "Bundled LiveKit",
        "doneWhen": (
            "Fleet Manager supervises LiveKit and mints tokens at adoption; guide 7 obsolete."
        ),
        "specRef": "version2.md section 5.1 and section 3.7",
        "stateFrom": "none",
    },
    {
        "id": "Mn+2",
        "title": "Production Fleet Manager",
        "doneWhen": (
            "Deployed as a PortainerCompose stack behind Traefik at framelink.huisman.io, with "
            "alerting."
        ),
        "specRef": "version2.md section 5.1 and section 3.8",
        "stateFrom": "none",
    },
    {
        "id": "Mn+3",
        "title": "Parity",
        "doneWhen": (
            "Stock image -> adopt -> fully green frame, mechanically equal to the frozen v1 "
            "reference. Deep, triple-checked verification. Only then do guides retire to the "
            "minimum set (section 8)."
        ),
        "specRef": "version2.md section 5.1 and section 8",
        "stateFrom": "none",
    },
]

#: The only resources that have ever converged on real hardware, each verified after a real
#: reboot rather than after a service restart. Everything the catalog has gained beyond these
#: is code with tests, which is a different and much weaker claim - keeping the two apart is
#: the single most load-bearing distinction in this file, because "implemented" reads like
#: "working" to a session that has forgotten which is which.
#:
#: Two witnessed sessions, kept in one list because the claim is identical:
#:
#:   2026-08-15, the M2 acceptance nine (first block below).
#:   2026-08-16, twenty-six more (second block), on mule T1RJ-6JCQ-9HN8-3920 during the first
#:     provision of the whole 79-resource catalog. The membership test was mechanical rather
#:     than eyeballed: a resource is listed only if the Fleet Manager's device-event history
#:     recorded a `boot` event for it - "Booted, and came back to verify X", which the loop
#:     emits only after a real reboot - AND it was `in-sync` in the live reconcile report at
#:     sequence 787. Either half alone is weaker: the boot event alone does not say the verify
#:     passed, and in-sync alone does not say a reboot was ever involved.
#:
#:   2026-08-16, one more (third block): tool.xvf-host.installed, whose evidence is the frame's
#:     own journal rather than the Fleet Manager's event history. It is called out separately
#:     because the membership test above could not be applied to it - by the time it converged,
#:     the report that would have shown it in-sync was the one corrupted by the stopped-pass
#:     census defect (9e3ecf2), and the frame's persistent journal had been rotated away by the
#:     Kiosk flood. What stands in their place is stronger for this one row rather than weaker:
#:     the loop's own "survived the reboot" verdict, which ResumePendingAsync emits only when
#:     the boot id has changed, plus the six installed files on disk.
#:
#: THE LIST IS A FLOOR, NOT A CENSUS, and the gap is evidence and not doubt. 72 of 79 were
#: in-sync at sequence 787; only these 36 can be shown to have been reboot-verified from the
#: evidence held, because /api/devices/{id}/events returns the last 50 events and the rest of
#: this frame's history had already rolled off before it was read. The other 36 in-sync
#: resources are very probably in the same condition and are deliberately not claimed.
_HARDWARE_VERIFIED_RESOURCES: list[str] = [
    # 2026-08-15 - M2's acceptance set.
    "boot.config.dtoverlay-waveshare-panel",
    "boot.cmdline.fbcon-rotate",
    "journal.storage-persistent",
    "agent.adoption",
    "agent.device-name",
    "identity.hostname",
    "unit.cpu-performance.content",
    "unit.cpu-performance.enabled",
    "cpu.governor.performance",
    # 2026-08-16 - the first provision of the full catalog, evidence as described above.
    "agent.keypair",
    "app.config.identity",
    "app.config.immich-kiosk-url",
    "app.config.livekit-token",
    "app.config.room",
    "apt.unattended-upgrades.allowed-origins",
    "boot.autologin.getty-tty1",
    "boot.config.dtoverlay-vc4-kms-v3d-noaudio",
    "camera.pipewire-node.framelink-cam",
    "kiosk.binary.pinned-release",
    "kiosk.config.immich-api-key",
    "kiosk.config.immich-url",
    "kiosk.config.offline-asset-count",
    "kiosk.config.offline-mode-enabled",
    "kiosk.listen-address",
    "kiosk.offline-cache.dir",
    "labwc.autostart.executable",
    "labwc.rc-xml.touch-map",
    "portal.permission-store.camera",
    "session.bash-profile-exec-labwc",
    "unit.chromium-kiosk.content",
    "unit.chromium-kiosk.enabled",
    "unit.framelink-camera.content",
    "unit.framelink-camera.enabled",
    "unit.xdg-desktop-portal.dropin-desktop",
    "wireplumber.conf.camera-monitors-disabled",
    # 2026-08-16 - the pin-and-verify fetch, on its first execution against the real upstream.
    # 173e321's Act ran for the first time at 07:16:41 CEST and the loop reported it "survived
    # the reboot" at 07:17:02: six files fetched from raw.githubusercontent.com at the pinned
    # commit 725f38464e73477a30aba9f5c220f1cfdc66d682, every SHA-256 verified before install,
    # nothing refused. On disk afterwards, with the modes the resource intends: xvf_host
    # 1,772,904 bytes -rwxr-xr-x, libcommand_map.so 151,680, libdevice_i2c.so 72,568,
    # libdevice_usb.so 73,312, dfu_cmds.yaml 2,507, transport_config.yaml 30. The frame's
    # outbound HTTPS was confirmed separately first (raw.githubusercontent.com answered 200 for
    # a real file, 6,043 bytes), so a failure here would have been the mechanism rather than the
    # network. This is the most reusable result of that session: it is the first proof that
    # fetch-pinned-and-digest-verified works from agent code against a live third party, which
    # is the mechanism every future third-party binary depends on.
    "tool.xvf-host.installed",
]

#: Measured facts that are neither a proven capability nor a converged resource: readings taken
#: off a real frame that **answer a question the catalog was carrying open**. They live here
#: rather than only in a commit message because a resuming session reads this file first, and a
#: question that has already been settled must not be investigated a third time.
#:
#: Each entry says what was read, on what, when, and - the part that earns the space - **what it
#: rules out**. A finding that only confirms a suspicion is worth a commit; a finding that closes
#: a branch of the search is worth the whole file.
_HARDWARE_FINDINGS: list[dict[str, str]] = [
    {
        "id": "wireplumber-applies-a-default-not-a-restore",
        "question": (
            "audio.mixer.pcm0-playback-volume is set to 60, survives a reboot, and is then put "
            "back to 37 (-23.00 dB) with wireplumber running. Is 37 a value WirePlumber has "
            "STORED for this device and is restoring, or is it WirePlumber's own compiled-in "
            "DEFAULT being applied to a device it has no stored opinion about? The catalog has "
            "carried this as blocked since it was written, and the two readings need opposite "
            "fixes."
        ),
        "measured": (
            "~/.local/state/wireplumber/ on the mule holds exactly ONE file, and it is "
            "`stream-properties`. There is no `restore-stream`, no `default-routes` and no "
            "`default-nodes` - which are the three files WirePlumber 0.5 writes when it has a "
            "stored per-device route volume to restore. The agent now names the files it finds "
            "rather than counting them, so this reading is in the frame's own drift text: "
            "`[wireplumber active, 1 stored device file (stream-properties)]`."
        ),
        "answer": (
            "It is a DEFAULT, not a restore. WirePlumber holds no stored route volume for this "
            "sink, so there is nothing stored to correct."
        ),
        "rulesOut": (
            "The catalog's original proposed fix - owning or clearing ~/.local/state/wireplumber/ "
            "- repairs nothing and must not be implemented. There is no file there to own. The "
            "correct fix is to set the volume THROUGH WirePlumber (audio.wireplumber."
            "playback-volume, wpctl), which overrides a default and is also what would make "
            "restore-device persist a stored one, so it is right under both readings."
        ),
        "consistentWith": (
            "The arithmetic, which remains arithmetic and not a measurement: WirePlumber 0.5's "
            "device.routes.default-sink-volume is 0.064 linear = -23.88 dB, and on this control's "
            "measured 1-step-per-decibel scale the nearest step at or above that is exactly 37. "
            "The file listing is the observation; the constant is the explanation it fits."
        ),
        "sequel": (
            "Later the same day, once audio.wireplumber.playback-volume finally executed and "
            "succeeded, **`default-routes` appeared in that directory beside "
            "`stream-properties`** - so WirePlumber now has a stored route volume where it had "
            "none, written by the agent's own `wpctl set-volume`. Both halves of this finding are "
            "therefore confirmed: it was applying a default because nothing was stored, and "
            "setting it through wpctl is what stores it."
        ),
        "whenUtc": "2026-08-16",
        "where": f"mule {MULE_USER}@{MULE_HOST}",
        "recordedIn": "reference/resource-catalog.md audio.wireplumber.playback-volume; commit 1ce597d",
    },
    {
        "id": "reconcile-loop-death-was-invisible-for-29-minutes",
        "question": (
            "The frame sat in loop_state=awaiting-reboot at report sequence 1112 and did not "
            "advance for 29 minutes, online and connected the whole time. Was the reboot floor "
            "(decision 79) declining a reboot and stranding the loop in a state only a reboot can "
            "leave?"
        ),
        "measured": (
            "No. The floor's own ledger read `reboots: []` and the journal file's mtime was "
            "identical to the pending-apply write that preceded the reboot request, so the "
            "floor's record-the-reboot write had never executed and its refusal branch had never "
            "been taken. The actual cause was captured by restarting the service and reading what "
            "the dying process printed: `System.ArgumentNullException: Value cannot be null. "
            "(Parameter 'reboots') at RebootFloor.Within(...) at RebootFloor.CrossAsync(...)`. "
            "The frame carried a journal written before decision 79 existed, so it had no "
            "`reboots` key; an absent key deserialises to null rather than to the empty list the "
            "property's initialiser appears to promise, and WhenWritingNull then omitted it again "
            "on every rewrite, so the omission could not heal."
        ),
        "answer": (
            "An upgrade-path crash inside the reconcile loop's task, plus a host that could not "
            "see it: AgentHost awaited its ten loops with Task.WhenAll, which surfaces nothing "
            "until every task finishes, and the other nine run for the life of the frame. The "
            "exception sat inside a completed task for 29 minutes and was finally printed by a "
            "shutdown a person triggered by hand."
        ),
        "rulesOut": (
            "The reboot floor as the cause of this class of stall, and the reboot floor's "
            "refusal path as a silent one - it logs and it was never reached. Do not design "
            "around a broken retry either: the retry path was measured working (budget reset "
            "3 -> 0, a fresh drift took it to 1)."
        ),
        "consistentWith": (
            "The same shape as the earlier escalation defect this session (a completed RunAsync, "
            "a live process, a live socket, a server still reporting the device online). That one "
            "was fixed in the loop; this one was the host-level half of the identical hole."
        ),
        "whenUtc": "2026-08-16",
        "where": f"mule {MULE_USER}@{MULE_HOST}, agent pid 4342",
        "recordedIn": (
            "ReconcileJournal.Normalise and AgentHost.WhenAllOrFirstFaultAsync, each with the "
            "test that reproduces the pre-fix behaviour"
        ),
    },
]

_HARDWARE_FINDINGS.append(
    {
        "id": "a-zero-countdown-reboots-the-frame-before-wireplumber-has-a-sink",
        "question": (
            "With the ordering corrected, audio.wireplumber.playback-volume ran on hardware for "
            "the first time - and failed every cycle: `wpctl get-volume @DEFAULT_AUDIO_SINK@` "
            "answered an empty string and `wpctl set-volume` was refused with **\"Translate ID "
            "error: '-1' is not a valid ID (returned by default-nodes-api)\"**. Is "
            "@DEFAULT_AUDIO_SINK@ simply the wrong way to address the sink on this frame?"
        ),
        "measured": (
            "**No, and the first answer to this was wrong.** The refusal is real but it is a "
            "symptom of *when* the question was asked, not of *how*. `repair.countdownSeconds` "
            "was 0, so each repair rebooted the frame ~21 s after boot - before WirePlumber had "
            "settled far enough to have a default sink, so the token had nothing to translate to. "
            "The fast cycle then fed itself: the login session never finished starting, which put "
            "session.bash-profile-exec-labwc and camera.pipewire-node.framelink-cam into drift "
            "too, each of which acted and rebooted in turn. 55 boots. Setting "
            "`repair.countdownSeconds` to 60 - the value ReconcileOptions already carries as its "
            "own default - was the whole fix. One further repair cycle and the frame reached "
            "**81 of 81 in sync, converged, 0 drifted, 0 blocked**, with photographs on the panel "
            "- and held it: eight consecutive converged censuses, telemetry sequence 1281 through "
            "1288, one every five minutes across 35 minutes, online throughout, with **no reboot "
            "between any of them**. That last part is the claim worth having, because everything "
            "this frame did wrong tonight it did by rebooting. Re-measured 90 minutes later, "
            "immediately before the deploy of 958ac9c and fe61154, from two records that do not "
            "share a source: the Fleet Manager's device-event history holds **no event of any kind "
            "after the `boot` at 2026-08-16T09:09:23Z** - no boot, no drift, and the 50 it does "
            "return are the 25 boot/25 drift pairs of the storm that preceded it - and census "
            "sequence 1296 at 10:25:05Z still read converged, 81 in sync, 0 drifted, 0 blocked, 0 "
            "reboots expected. **The frame's own process table is the stronger witness**, because "
            "it is not the server's word: chromium pid 1246 had been up continuously since "
            "09:09:28Z and was still 5,306 s old at 10:37:54Z, so the 88 quiet minutes are attested "
            "by a process a reboot would have killed."
        ),
        "answer": (
            "@DEFAULT_AUDIO_SINK@ is correct and works. `wpctl get-volume @DEFAULT_AUDIO_SINK@` "
            "now answers `Volume: 1.00`, PCM,0 reads `60 [100%] [0.00dB] [on]` and holds across a "
            "boot for the first time in this project's history, and PCM,1 reads the same."
        ),
        "rulesOut": (
            "Renaming the sink, addressing it by id or node.name, a config fragment, and "
            "api.alsa.soft-mixer - none of them is needed. It also rules out reading a repair "
            "failure on a fast-cycling frame as a fact about the repair: three of the four "
            "resources that looked broken here were only ever being asked too early."
        ),
        "consistentWith": (
            "The finding above, and it closes the loop on it. Before the repair succeeded, "
            "~/.local/state/wireplumber/ held only `stream-properties` - no stored route volume, "
            "so a default was being applied. **After the first successful `wpctl set-volume`, "
            "`default-routes` appeared in that directory.** That is decision 80's stated reasoning "
            "confirmed on hardware: setting the volume *through* WirePlumber is what makes it "
            "persist a stored one, so the value now survives a boot by WirePlumber's own "
            "mechanism rather than by the agent winning a race against it."
        ),
        "whenUtc": "2026-08-16",
        "where": f"mule {MULE_USER}@{MULE_HOST}",
        "recordedIn": (
            "fleet setting repair.countdownSeconds 0 -> 60 (revision 9); the reconcile journal's "
            "ledger entry for audio.wireplumber.playback-volume carried the refusal text verbatim "
            "in its `change` field while it was failing. **The photograph itself is "
            "tools/harness/runs/20260816T091021Z-collect/screenshot.png**, a grim capture off the "
            "live Wayland surface at 09:10:21Z - 58 seconds after that last boot - and it is the "
            "first photograph this project has put on a panel under agent control. That is a "
            "measurement rather than a flourish: every earlier screenshot under runs/ is the "
            "console stage or the repair screen, and the largest of them "
            "(20260816T061834Z-collect, 194,701 bytes) is the composed-headline defect itself - "
            "'Everything is working / This frame is adopted, up to date and showing your photos' "
            "printed directly above 'unit.chromium-kiosk.running-matches-content failed after 3 "
            "tries' and a Try again button, on a panel showing no photographs at all. runs/ is "
            "gitignored, so these paths are local-only evidence"
        ),
    }
)

_HARDWARE_FINDINGS.append(
    {
        "id": "the-fleet-list-told-the-truth-about-a-frame-for-the-first-time",
        "question": (
            "`fe61154` computes the agent's self-report from the loop's own `loopState` and pushes "
            "it when it changes, instead of composing one `Progressing(...)` string at startup and "
            "never recomputing it. Every frame therefore told its operator it was *part-way through "
            "applying something*, always, and a frame that had given up read the same. Does the "
            "operator's own device list actually read as in sync once a frame converges - on "
            "hardware, not in a test host?"
        ),
        "measured": (
            "**Yes, and both directions of §2.6's honesty rule were exercised in the same 40 "
            "seconds.** Device T1RJ-6JCQ-9HN8-3920 against the dev Fleet Manager, four readings of "
            "`GET /api/devices`, each one paired with the census `GET /api/devices/{id}/reconcile` "
            "reported at the same instant:\n"
            "  * 10:35:01Z, agent `0.0.0+1ce597d.dirty` - `agentStatus: \"Progressing(linux-arm64, "
            "endpoints resolved by boot-file)\"`, `health: \"working\"`, while its own census "
            "(sequence 1297) read `loopState: converged, inSync 81, drifted 0, blocked 0`. **That "
            "is the defect, captured live on a frame that was completely converged.**\n"
            "  * 10:35:04Z, agent `0.0.0+fe61154.dirty`, one second after the new binary's "
            "handshake and before its first census - `agentStatus: \"linux-arm64, endpoints "
            "resolved by boot-file\"` with **no vocabulary head at all**, classifying as `health: "
            "\"unknown\"`. This is the arm that matters most and is the easiest to mistake for a "
            "regression: *I have not looked at myself yet* is a real answer, and `InSync` before "
            "the first observation would break §2.6 in the direction that hides trouble.\n"
            "  * 10:35:13Z - back to `Progressing(...)` / `working`, and now **true**: census 1298 "
            "read `loopState: backing-off, inSync 80, drifted 0, blocked 1, rebootsExpected 1`.\n"
            "  * 10:35:43Z - `agentStatus: \"InSync(linux-arm64, endpoints resolved by boot-file)\"`, "
            "`health: \"in-sync\"`, census 1299 `loopState: converged, inSync 81, drifted 0, blocked "
            "0, rebootsExpected 0`.\n"
            "The frame narrated both transitions in its own journal - `This frame now reports itself "
            "as Progressing(...)` and then `... as InSync(...)` - so the push fired from the loop "
            "rather than from a reconnect. **`last_seen_utc` stayed at 2026-08-16T10:35:03.0074154Z "
            "across both pushes**, which is fe61154's stated intent verified rather than assumed: "
            "the column is the last proven handshake and doubles as the offline clock, and a status "
            "update must not touch it."
        ),
        "answer": (
            "The operator's device list is correct about a frame for the first time in this "
            "project. A converged frame reads *in sync*; a frame that has not observed itself yet "
            "reads *unknown* rather than claiming convergence; and `Progressing` now appears only "
            "while the loop is actually progressing."
        ),
        "rulesOut": (
            "The hello alone as a sufficient carrier, confirmed on hardware rather than argued: "
            "the frame's session opened at 10:35:03Z and both later values arrived over it with no "
            "reconnect, so a per-handshake fix would have pinned this row to `unknown` - whatever "
            "the loop was doing in its first second - for the entire life of the session. It also "
            "rules out reading `health: \"unknown\"` on a freshly restarted frame as a fault; it is "
            "the designed answer and lasts about ten seconds."
        ),
        "consistentWith": (
            "The three renderings before it - the animation, the headline (c3116bc) and the accent "
            "(958ac9c) - all of which derived a user-visible property from something other than "
            "what the frame had observed of itself. This is the fourth and the only one an "
            "operator sees before deciding whether to look at a frame at all."
        ),
        "whenUtc": "2026-08-16",
        "where": f"mule {MULE_USER}@{MULE_HOST}, dev Fleet Manager at {CONTROL_URL}",
        "recordedIn": (
            "commit fe61154. The readings are "
            "tools/harness/runs/20260816T103511Z-repairing-window/fleet-timeline.jsonl (the deploy "
            "window, one sample every 3 s) and hold-timeline.jsonl beside it (the 20 minutes "
            "after, one every 10 s: censuses 1299-1303, five consecutive converged passes across "
            "104 readings with zero deviations, and no reboot - the frame's uptime and chromium "
            "pid 1246 both still date from 09:09Z). The post-deploy panel is "
            "tools/harness/runs/20260816T103808Z-collect/screenshot.png. runs/ is gitignored, so "
            "these are local-only evidence"
        ),
    }
)

_HARDWARE_FINDINGS.append(
    {
        "id": "an-agent-restart-does-not-reload-the-page-the-agent-serves",
        "question": (
            "`958ac9c` gives the browser stage an accent: the agent composes a colour name and "
            "`app/frame-stage.js` draws it as a dot beside the headline. After deploying it, the "
            "frame was caught mid-repair with the composed headline **'Putting this frame right'** "
            "on the panel - and no dot of any colour. Did the accent fail to compose?"
        ),
        "measured": (
            "**No. The agent's half shipped and the page's half did not run, because the page was "
            "never re-fetched.** Three readings, all read-only:\n"
            "  * The panel capture at 10:35:14Z is **entirely greyscale** - every one of its twelve "
            "most common colours is (n, n, n), and a scan for saturated warm pixels finds zero. So "
            "there was no accent rather than a wrong one.\n"
            "  * The agent *is* serving the new page: `curl http://127.0.0.1:8888/frame-stage.js` "
            "on the frame returns 12,298 bytes containing the `ACCENTS` map and `amber: '#f0a52a'` "
            "verbatim.\n"
            "  * **chromium pid 1246 started at 09:09:28Z and was still 5,306 s old at 10:37:54Z**, "
            "while `fl-agent` restarted at 10:35:10Z. The browser is 86 minutes older than the "
            "binary serving it, so the document it is running was fetched from the *previous* "
            "agent - one with no `ACCENTS` map and no `headline()` function. The journal says so "
            "too: `The page checked in after 0 s`, which is a websocket reconnect, not a load.\n"
            "The headline updated because it is composed server-side and arrives as text over that "
            "reconnected socket; the dot needs client code the running document does not have."
        ),
        "answer": (
            "`fl.py deploy` restarts `fl-agent` and `fl-agent` restarts Immich Kiosk (pid 8370 at "
            "10:35:10Z), but **nothing reloads chromium**. Any change to `app/frame-stage.js` is "
            "therefore invisible on the panel until the browser itself is restarted or the frame "
            "reboots, however many times the agent is redeployed."
        ),
        "rulesOut": (
            "A defect in `StagePalette`, in `BrowserStage.Compose` or in the accent's composition - "
            "none of them was reached, and none should be changed on this evidence. It also rules "
            "out reading 'no amber on the panel after deploying 958ac9c' as the fix failing: the "
            "amber arm has still never been *seen*, and the next repair window on this frame will "
            "still show no dot until chromium is reloaded. Anyone verifying a page-side change must "
            "restart the browser first, or they will measure the old document and blame the new "
            "agent."
        ),
        "consistentWith": (
            "The pre-fix screenshot 20260816T061834Z-collect/screenshot.png, which shows the same "
            "page with no accent beside a headline - 958ac9c's own note that 'the page had no "
            "accent at all before this'. The captures either side of the deploy are the same "
            "document, which is exactly the point."
        ),
        "whenUtc": "2026-08-16",
        "where": f"mule {MULE_USER}@{MULE_HOST}, panel on card0-DSI-2",
        "recordedIn": (
            "commit 958ac9c. The capture the greyscale scan was run over is "
            "tools/harness/runs/20260816T103511Z-repairing-window/"
            "panel-103514-putting-this-frame-right.png, with the stage appearing 3 s earlier and "
            "photographs returning 30 s later in the same directory; samples.txt there is the "
            "whole 2-second series with the frame's journal beside each shot. runs/ is gitignored, "
            "so these are local-only evidence"
        ),
    }
)

_HARDWARE_FINDINGS.append(
    {
        "id": "the-page-refresh-stood-down-on-the-update-that-introduced-it",
        "question": (
            "`0384c01` adds §2.10's fifth supervised behaviour: the page reports its own document "
            "age, the agent compares it against its process uptime, and a document a *previous* "
            "agent served is told to `location.reload()`. Decision 84's roll-out safety property is "
            "that the behaviour is **inert for the update that introduces it** - the running "
            "document predates the reporting half, so it cannot say how old it is, so the verdict "
            "is `Unknown` and nothing happens. That property has to hold on the first deploy or it "
            "never holds at all: if the acting half can fire against a page that has no `inCall` to "
            "report, the first frame it ever touches is the one least able to defend itself. Does "
            "it hold on hardware?"
        ),
        "measured": (
            "**It held, and the stand-down was total: not one supervision action of any kind for "
            "the whole boot.** Deployed to T1RJ-6JCQ-9HN8-3920 at 11:38:39Z, `fl-agent` active "
            "again at 11:38:48Z as `0.0.0+0384c01.dirty`, remote `sha256sum` "
            "`b7f6f5a181ea5213...` equal to the built binary. Four readings, all read-only:\n"
            "  * **`/var/lib/fl-agent/app-build` does not exist** - checked at T+0, T+14 min and "
            "T+33 min, and `find /var/lib/fl-agent -newermt <deploy>` lists only "
            "`agent-memory.json`, `reconcile-journal.json` and `kiosk/offline-assets`. "
            "`PageFreshness.Record()` runs on a `Fresh` verdict and on nothing else, so an absent "
            "record *is* the `Unknown` verdict, sampled three times over half an hour.\n"
            "  * **`journalctl -u fl-agent.service -b | grep -c 'Supervision ('` is `0`.** No "
            "`page-refresh`, and no reload command was published: the only page-shaped narration "
            "this boot is `The page checked in after 0 s`, which is the WebSocket reconnect "
            "decision 84 exists to distinguish from a load.\n"
            "  * **chromium pid 1246 still dates from 11:09:28**, 10,992 s old at 12:12Z against a "
            "boot at 11:09:13 - the browser was neither restarted nor reloaded across an agent "
            "restart 2.5 hours younger than it.\n"
            "  * **Both halves were live, so the silence is a stand-down and not an absence.** The "
            "frame is serving the new page - `frame-stage.js` is 15,576 bytes where it was 12,298, "
            "and carries `documentAgeMs` - and `EmbeddedApp.BuildId` **recomputed from the bytes "
            "the frame itself serves** (path + NUL + content over the eight assets, ordinal order, "
            "SHA-256 truncated to 16 hex) is `8c8a78a905da2e5c`, identical to the digest computed "
            "from the repository's `app/` tree. The acting half is in the running ELF too: "
            "`strings -e l /usr/local/bin/fl-agent` finds `The page on this frame is running app`, "
            "`expected the page to be running app`, `tell the page to reload` and `so it runs app`.\n"
            "**Nothing regressed.** The 55 minutes after the deploy are 111 consecutive censuses, "
            "11:39:16Z - 12:34:17Z, one every 30 s, every one `converged / inSync 81 / drifted 0 / "
            "blocked 0 / rebootsExpected 0` with `health: in-sync`, spanning **eleven distinct "
            "sequences (1313-1323)** so it is eleven full reconcile passes rather than one cached "
            "report, with **zero deviations after the restart**. The one deviation in the whole "
            "run is the restart transient at 11:38:46Z - `backing-off, inSync 80, blocked 1 "
            "(agent.version), rebootsExpected 1` - gone by the next reading, the same shape the "
            "`fe61154` deploy showed. Three `grim` captures at 11:40Z, 11:53Z and 12:13Z are colour "
            "photographs (mean channel spread 35.6 / 41.3 / 42.7), each a different photograph, so "
            "the slideshow kept advancing inside the un-reloaded document. `repair.countdownSeconds "
            "= 60` and `slideshow.albums = shared` unchanged at settings revision 9."
        ),
        "answer": (
            "**The roll-out safety property is true on hardware.** The first page this behaviour "
            "could ever reload is one loaded under a binary that already reports its own call "
            "state, exactly as decision 84 claims. **The corollary is the part worth writing down: "
            "`app-build` is *not* written by the deploy that introduces the behaviour, and its "
            "absence is the proof that the behaviour worked** - not evidence that the deploy "
            "failed. The record can only appear once something reloads the browser, which on this "
            "frame means a reboot or the 03:00 restart; until then the agent knows nothing about "
            "the document and says nothing about it."
        ),
        "rulesOut": (
            "Reading an absent `/var/lib/fl-agent/app-build` after deploying `0384c01` as a broken "
            "deploy, a state-directory permission problem, or a supervisor that failed to start - "
            "it is the designed `Unknown` branch and the only branch reachable here. It also rules "
            "out expecting any narration of that verdict: `PageFreshness.Observe` logs on the "
            "record and `Supervisor.PageTickAsync` logs on the action, and `Unknown` reaches "
            "neither, so **the agent's honest report of concluding nothing is an empty journal**. "
            "What it does *not* establish is the acting half itself: no page has yet reported a "
            "document age on this frame, so `Fresh`, `Stale`, the reload, the cooldown and the "
            "in-call deferral remain unexercised on hardware and are still carried only by the "
            "suite's 1223 tests. The reboot arm of the record - that `app-build` survives one - is "
            "**undetermined**: the frame did not reboot inside the window (uptime unbroken from "
            "11:09:13) and was deliberately not made to."
        ),
        "consistentWith": (
            "The finding directly above it, which is the defect this closes: chromium pid 1246 and "
            "boot 11:09:13 are the *same* browser and the *same* document that finding caught "
            "running 86 minutes behind the agent serving it, still running now, two agent restarts "
            "later. Nothing about this deploy changed that, and that is the point - the served app "
            "moved from `5578e1d1...` to `d1184f45...` (frame-stage.js) while the document on the "
            "glass did not, which is decision 84's premise reproduced rather than assumed."
        ),
        "whenUtc": "2026-08-16",
        "where": f"mule {MULE_USER}@{MULE_HOST}, dev Fleet Manager at {CONTROL_URL}",
        "recordedIn": (
            "commit 0384c01, deployed as 0.0.0+0384c01.dirty (the `.dirty` is `fl.py`'s own "
            "progress.json in-flight marker, as on fe61154). Gates before it: "
            "`dotnet run --project tests/FrameLink.Tests -c Release` 1223 passed / 0 failed, and "
            "`dotnet build FrameLink.slnx -c Release` 0 warnings / 0 errors. The census series is "
            "C:/Users/jori/framelink-scratch/v-census.jsonl - 120 readings, one every 30 s, nine "
            "before the deploy and 111 after it - the "
            "panel captures are v-panel-before.png and v-panel-after-1..3.png beside "
            "it, the whole post-deploy journal is v-journal-after.txt, and the deploy transcript is "
            "v-deploy.log. That directory is outside the repository, so these are local-only "
            "evidence. The served release feed was checked after deploying - "
            "GET /agent/release/linux-arm64 answers 0.0.0+0384c01.dirty with the matching sha256, "
            "so §2.8 cannot walk the frame back to the previous build"
        ),
    }
)

_HARDWARE_FINDINGS.append(
    {
        "id": "the-array-names-its-build-and-nothing-public-can-name-it-back",
        "question": (
            "Decision 90 records that upstream published `v2.0.10` **twice with different "
            "bytes** - `17bac32a` (2026-06-29, sha256 `237f762a…`) and `aeacafab` (2026-07-13, "
            "sha256 `81593709…`), 43 % of 933,888 bytes differing - and that Frame #1 is "
            "*probably* running the newer one because it was flashed 2026-08-08, **which is "
            "inference from dates and not a measurement**. The flashed file itself is gone: the "
            "SD card that ran it was replaced. But the pinned `libcommand_map.so` carries "
            "`BLD_REPO_HASH`, *\"Retrieve the GIT hash of the sw_xvf3800 repo used to build the "
            "firmware\"*, beside `BLD_MSG`, `BLD_HOST` and `BLD_MODIFIED` - four reads no "
            "session had ever taken. Can the board say which build it is running?"
        ),
        "measured": (
            "**The board answers, precisely and repeatably - and nothing public can decode the "
            "answer.** Frame #1's own array (serial `101991441260500069`, `bcdDevice 020a`), "
            "`fl-agent` proven stopped both times, read twice nine minutes apart across a full "
            "stop/start cycle and byte-identical on both:\n"
            "```\n"
            "VERSION 2 0 10\n"
            "BLD_REPO_HASH 3f08f630b41b8bce11cb2f45857ba49f22f9d507\n"
            "BLD_MSG ua-io16-sqr\n"
            "BLD_HOST NA\n"
            "BLD_MODIFIED TRUE\n"
            "```\n"
            "`BLD_MSG`, `BLD_HOST` and `BLD_MODIFIED` arrive NUL-padded to fixed widths (39, 28 "
            "and 2 NULs); the tool prints them raw and they look like spaces, so `xvf.parse_reply` "
            "strips them - `str.split()` does not, and without that the padding lands inside the "
            "recorded value and any later board-to-board comparison compares the padding too.\n"
            "**`sw_xvf3800` does not exist in public.** `github.com/xmos/sw_xvf3800` and "
            "`api.github.com/repos/xmos/sw_xvf3800` both answer **404**; a GitHub repository "
            "search for `sw_xvf3800` returns **nothing, from any owner**; `xmos` publishes "
            "exactly one XVF repository and it is `host_xvf_control`. GitHub commit search for "
            "`3f08f630b41b8bce11cb2f45857ba49f22f9d507` returns nothing and neither does a web "
            "search. XMOS's own build documentation describes the firmware as a **source release "
            "package**, not a repository - which is what makes the field unresolvable rather "
            "than merely unindexed.\n"
            "**The two published binaries cannot be tied to source commits by inspection "
            "either, and the reason is structural.** Both were downloaded and their digests "
            "confirmed against decision 90's (`237f762a5562…`, `81593709500c…`, 933,888 bytes "
            "each, 402,246 bytes = 43.1 % differing). Neither contains the hash as ASCII, as "
            "upper-case ASCII, as 20 raw bytes, word-swapped, nor the string `ua-io16-sqr` - "
            "under identity, all 256 XOR constants, all 255 ADD constants, bit-reversal, or "
            "strides of 2, 3, 4 and 8. There are only **331 printable ASCII runs of 6+ chars in "
            "933,888 bytes** and not one is a firmware string. The layout says why: a 98-byte "
            "header, then bytes `0x62`-`0x33fa5` **identical between the two builds** at 1.835 "
            "bits/byte, then a body at 5.649 bits/byte whose non-padding bytes run **7.327 "
            "bits/byte** - the application payload is compressed. That is simultaneously why no "
            "string is findable and why a small source change flips 43 % of the file.\n"
            "**Both publications were real firmware changes, so their hashes must differ.** "
            "`17bac32a` is *\"restore audio descriptors and buffers on bus reset\"*, `aeacafab` "
            "is *\"decoupling DOA state from effect\"*, and upstream's "
            "`xmos_firmwares/usb/changelog.md` lists all three v2.0.10 fixes under one heading - "
            "the union of the two, with no note that the version was published twice."
        ),
        "answer": (
            "**The reading is a per-build fingerprint that names no build.** It is stable, "
            "reproducible, free, needs no flash and works on any board whose USB is up - so it "
            "tells two boards apart with certainty - but `3f08f630b41b8bce11cb2f45857ba49f22f9d507` "
            "resolves to nothing anyone outside XMOS can look up. **`BLD_MODIFIED TRUE` is the "
            "part that settles the shape of the question permanently**: upstream's own wording is "
            "*\"whether or not the current firmware repo has been modified from the official "
            "release\"*, so this image was built from a **dirty working tree**. Even a public "
            "`sw_xvf3800` would let the hash name a base commit and not the bytes - so a git hash "
            "can never be a byte-level identity for these images, and no future access to the "
            "repository would change that.\n"
            "**What it does newly establish is the build configuration.** `BLD_MSG` is "
            "`ua-io16-sqr` - the 2-channel 16 kHz square-array USB Audio profile - which rules "
            "out Frame #1 running any of the four sibling images upstream ships in the same "
            "directory (`_6chl_v2.0.8`, `_v2.0.9_48k`, `_v2.1.0_16k6ch`, `_v2.1.0_48k2ch`). "
            "Upstream's v2.0.8 changelog names the six-channel profile `ua-io16-6ch-sqr`, which "
            "is what makes the field interpretable at all. `BLD_HOST` is `NA`, so the build host "
            "is not recorded and corroborates nothing.\n"
            "**The operator's decision gets a decisive experiment out of this, which it did not "
            "have before.** If the spare is ever flashed, flash it from *one named published "
            "file* and read `BLD_REPO_HASH` afterwards: equal to `3f08f630…` means Frame #1 runs "
            "that file's build, unequal means it runs the other one. Either outcome closes the "
            "question outright, from a flash that was going to happen anyway - and it also makes "
            "every future flash self-documenting, because the file-to-hash mapping is established "
            "the first time anyone records both."
        ),
        "rulesOut": (
            "Rules out **reading the published `.bin` files to find out what is on a board**. "
            "They are compressed and no simple transform exposes a string; the only way further "
            "is decompressing an XMOS boot image, and decision 63 forecloses that deliberately - "
            "staying not-an-XMOS-Licensee is a precondition of the native-protocol work, and "
            "reverse-engineering their image format is the wrong side of that line. This is a "
            "stopping point taken on purpose, not effort not spent.\n"
            "It also rules out `BLD_REPO_HASH` ever being a *byte* identity, per `BLD_MODIFIED "
            "TRUE` above.\n"
            "**What it does not establish: which of the two 2.0.10 builds Frame #1 is running.** "
            "Decision 90's date inference - flashed 2026-08-08, after the 2026-07-13 "
            "republication, so probably the newer build - is untouched and is still inference. "
            "Nothing here bears on board revision either; that remains silkscreen-only. And the "
            "2.0.6 spare in the unpowered Frame #2 was not read, so there is exactly one "
            "fingerprint on file and nothing yet to compare it against."
        ),
        "consistentWith": (
            "Decision 90 throughout, and it reproduces that decision's bench measurement exactly: "
            "`GPO_READ_VALUES 0 0 0 1 0`, three stable readings, on both passes - the same five "
            "values the 2026-08-20 session read from a factory 2.0.6 array and from this 2.0.10 "
            "one. That session is still visible in this boot's `dmesg`: the array re-enumerated "
            "at 20190.997 s with `bcdDevice= 2.06` and again at 20295.380 s with `bcdDevice= "
            "2.0a`, which is the spare going in and Frame #1's own array coming back. It also "
            "confirms decision 90's claim that the command map carries these four commands - they "
            "are at offsets 504-510 of `libcommand_map.so`'s string table, with the descriptions "
            "quoted above."
        ),
        "whenUtc": "2026-08-23",
        "where": f"mule {MULE_USER}@{MULE_HOST}, array serial 101991441260500069",
        "recordedIn": (
            "`tools/harness/flh/xvf.py`, whose read allowlist gained the four `BLD_*` commands "
            "and, with them, two gates that sit *beside* the allowlist rather than behind it: a "
            "command must be one bare `A-Z0-9_` token (so no value can follow it, which is how "
            "`xvf_host` turns a getter into a setter) and must contain none of `WRITE_FRAGMENTS`, "
            "**checked without consulting the allowlist** - so adding `GPO_WRITE_VALUE` or "
            "`DFU_DETACH` to `READ_COMMANDS` still raises. Verified by doing exactly that in a "
            "scratch interpreter. No test suite covers `tools/harness/`, so the gates were "
            "exercised directly and the module was then run end-to-end against the frame twice. "
            "Two artifacts under the gitignored `tools/harness/runs/`, so local-only evidence: "
            "`20260823T153820Z-array` (first reading) and `20260823T154709Z-array` (second, with "
            "the NUL strip and with `certified` written *into* `reading.json` instead of being "
            "assigned after the file was closed). Frame #1 was stopped twice for 50 s and 44 s, "
            "reported `InSync(linux-arm64, endpoints resolved by boot-file)` on both restarts, "
            "and chromium pid 243393 survived both untouched - the browser was never reloaded. "
            "Photographs before and after each cycle are the `*-collect` directories beside them. "
            "Neither reading is `certified`: the array was attached long before the agent stopped, "
            "so the connect-order proof is `violated` by construction. That bounds the *GPO* "
            "reading only - `BLD_*` are fixed at firmware compile time and nothing a running "
            "agent can send moves them."
        ),
    }
)

#: The durable artifacts a session starting from nothing needs, and what each one is *for*.
#: The progress file is deliberately not the specification; it is the pointer to it.
_ORIENTATION: dict[str, str] = {
    "specification": (
        "version2.md - the build specification. Section 5.1 is the milestone ladder mirrored "
        "below, section 5.5 is why this file exists, Appendix A preserves every decision with "
        "its reasoning."
    ),
    "resourceSpec": (
        "reference/resource-catalog.md - the enumeration of all 80 device settings extracted "
        "from build guides 3-12 and the cross-guide section, one block per resource with its "
        "Observe, Act and Verify. This is what M3 migrated, and it is the spec each new "
        "resource is written against. The count is the file's own Total row, which is what "
        "the resources ledger below reads."
    ),
    "parityTarget": (
        "reference/v1-state-inventory.txt - the frozen v1 frame's state: packages, units, "
        "config contents and hashes, mixer values, firmware versions. This is what Mn+3 "
        "measures 'at parity' against, and the catalog's cross-check."
    ),
    "parityHarness": (
        "tools/harness/fl.py parity - the first of Mn+3's three bars. It runs read-only probes "
        "over SSH, then tools/FrameLink.Parity judges the result against the parity target, an "
        "expected-difference ledger (tools/FrameLink.Parity/expected-differences.json) and the "
        "catalog. `fl.py parity --coverage` needs no frame and prints what a state diff can and "
        "cannot answer. Exit 0 at parity, 2 differences found, 3 the comparison could not be "
        "completed."
    ),
    "reasoningRecord": (
        "git log - every commit message carries why, not what. `git log --oneline` is the "
        "cheapest orientation available and the only place some findings exist at all (the "
        "debugfs-exits-0-on-failure trap is ba5a873; the StartLimitIntervalSec section trap is "
        "bebf34c)."
    ),
    "repoRules": (
        "CLAUDE.md - binding operational rules. Section 1.2 on credentials is absolute, "
        "section 1.8 requires explicit per-class authorisation before any mutation of the mule."
    ),
    "harness": (
        "tools/harness/fl.py - this harness. `--help` on any subcommand explains what it does "
        "and what it refuses to do; flh/config.py holds every host, path and default in one "
        "place."
    ),
    "openItems": (
        "TODO.md is v1's outstanding work and is gitignored; version2.md Appendix B holds v2's "
        "open items."
    ),
}


def utcnow() -> str:
    """Timestamp in the one format the whole file uses: RFC 3339, UTC, second precision."""
    return datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def _read_number(path: str, pattern: str) -> tuple[int | None, str]:
    """Pull one integer out of a repository file. Returns the value and how it was obtained.

    Never guesses. A file that cannot be read, or that no longer contains the pattern, yields
    ``None`` and a sentence saying so - which is a useful fact in itself, because it means the
    thing that used to be authoritative for that number has moved.
    """
    target = REPO_ROOT / path
    try:
        text = target.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        return None, f"{path} could not be read ({exc.__class__.__name__})"
    match = re.search(pattern, text)
    if not match:
        return None, f"{path} no longer contains the pattern this count is read from"
    return int(match.group(1)), path


def _resource_ledger() -> dict[str, Any]:
    """The M3 ledger, measured from the repository rather than recorded.

    Two numbers, each read from the file that is authoritative for it:

    * the catalog total, from the Counts table in ``reference/resource-catalog.md``;
    * how many are implemented, from the assertion in ``AgentResourceGraphTests.cs`` that
      pins ``graph.Count`` - which is not a proxy for the count, it is the count.

    Measuring rather than recording is the point. A frozen number would be wrong within a day
    of a workstream landing resources, and wrong in the direction that matters least visibly:
    it would keep claiming less progress than exists, and nobody re-checks a number that only
    ever understates.

    **The graph is no longer a subset of the catalog, and the arithmetic used to assume it
    was.** ``kiosk.config.albums`` was added to the agent deliberately without being added to
    ``reference/resource-catalog.md`` (see c855027): it comes from neither guide 9 nor the
    catalog, it exists because a frame whose account owns no photographs can only reach any
    through a shared album. So implemented legitimately exceeded the catalog total and
    ``remaining`` went to -1, which the frontier line then rendered as "80 of 79 catalog
    resources are implemented" - a sentence that reads as a bug in the counting rather than as
    the real thing it was describing. Both numbers were right the whole time; the subtraction
    was not. ``remaining`` is now floored at zero and the excess is reported as
    ``beyondCatalog``, so an agent that carries more than the catalog is visible as itself
    rather than as a negative.

    **``beyondCatalog`` is a net and not a membership count**, and it is worth saying so where
    it is computed. It is ``implemented - catalogTotal``. Today that is 81 - 80 = 1, while the
    memberships behind it are *two* resources the catalog does not carry
    (``agent.device-name``, ``kiosk.config.albums``) less *one* the catalog carries and the
    agent deliberately does not (``pkg.git``, excluded by the catalog's open question 3). The
    two sets are named in ``AgentResourceGraphTests`` and asserted there; this number cannot
    see them and must not be read as though it could.
    """
    total, total_source = _read_number(
        "reference/resource-catalog.md",
        r"\|\s*\*\*Total\*\*\s*\|\s*\*\*(\d+)\*\*\s*\|",
    )
    implemented, implemented_source = _read_number(
        "tests/FrameLink.Tests/AgentResourceGraphTests.cs",
        r"Assert\.Equal\(\s*(\d+)\s*,\s*graph\.Count\s*\)",
    )
    verified = list(_HARDWARE_VERIFIED_RESOURCES)
    ledger: dict[str, Any] = {
        "catalogTotal": total,
        "catalogTotalReadFrom": total_source,
        "implemented": implemented,
        "implementedReadFrom": implemented_source,
        "remaining": max(0, total - implemented) if (total is not None and implemented is not None) else None,
        "beyondCatalog": max(0, implemented - total) if (total is not None and implemented is not None) else None,
        "hardwareVerifiedCount": len(verified),
        "hardwareVerified": verified,
        "meaning": {
            "implemented": (
                "The resource exists in the agent's catalog with tests. It has never necessarily "
                "run on a frame."
            ),
            "hardwareVerified": (
                "The resource converged on the mule and was verified after a real reboot, and "
                "the evidence for both halves is still held. This is a floor and not a census - "
                "see the comment on _HARDWARE_VERIFIED_RESOURCES - so it undercounts, and it is "
                "a strictly stronger claim than the in-sync count a frame reports."
            ),
        },
        "gap": (
            "The first full provision has happened: on 2026-08-16 the mule reached 81 of 81 "
            "in sync, converged, 0 drifted, 0 blocked, with photographs on the panel - see the "
            "WirePlumber finding. What is still not held is the *evidence* for most of it: "
            "hardwareVerified is the subset that can still be shown to have been reboot-verified "
            "from records not yet rolled off, so the gap now is proof rather than convergence."
        ),
    }
    return ledger


def _environment() -> dict[str, Any]:
    """Hosts, credentials by name, and the measurements other timeouts are set against.

    Credential **values** never appear here, in any form, ever (CLAUDE.md section 1.2). What
    appears is the variable name, what it unlocks and where to get one, which is what a
    session that has just started actually lacks.
    """
    return {
        "mule": {
            "address": f"{MULE_USER}@{MULE_HOST}",
            "what": (
                "The development mule: the existing frame, repurposed. Raspberry Pi 5, Raspberry "
                "Pi OS Lite (Trixie), 800x1280 DSI panel on card0-DSI-2, on a controllable smart "
                "plug."
            ),
            "agentUnit": "fl-agent.service, running as root, binary at /usr/local/bin/fl-agent",
            "override": "FL_HOST / FL_USER",
        },
        "fleetManager": {
            "address": CONTROL_URL,
            "what": (
                "The dev Fleet Manager, run on the workstation from src/FrameLink.Control. The "
                "mule's agent is pointed here and retries forever while it is down, which is why "
                "a journal capture taken with the server stopped is a wall of connection "
                "warnings rather than a fault."
            ),
            "startedBy": (
                "dotnet run --project src/FrameLink.Control -- --urls http://0.0.0.0:5199, with "
                "FRAMELINK_OPERATOR_PASSWORD set and FRAMELINK_DATA_DIR pointing at the operator "
                "data directory outside this repository."
            ),
            "agentPointedBy": "--control-url on `fl-agent install`, or the FL_CONTROL_URL variable",
        },
        "homeAssistant": {
            "address": HA_URL,
            "entity": HA_ENTITY,
            "what": (
                "Switches the mule's power for `fl.py power`. Port 8123 was verified against the "
                "live instance; 8086 on the same host is a different service that answers 404 for "
                "every path, which once read exactly like 'that entity does not exist'."
            ),
            "override": "FL_HA_URL / FL_HA_ENTITY",
        },
        "credentials": [
            {
                "variable": "FL_PW",
                "unlocks": "fl.py deploy, fl.py collect - and it is also the answer to sudo on the mule",
                "howToObtain": "Ask the user. Supplied inline per session; there is deliberately no key-based fallback.",
            },
            {
                "variable": "FL_HA_TOKEN",
                "unlocks": "fl.py power",
                "howToObtain": f"A long-lived access token from the Home Assistant profile page at {HA_URL}",
            },
            {
                "variable": "FRAMELINK_OPERATOR_PASSWORD",
                "unlocks": "the Fleet Manager GUI - the single operator credential of version2.md section 3.2",
                "howToObtain": (
                    "Ask the user. An instance started without it still runs and serves a setup "
                    "page naming the variable, and answers connecting frames 'not-configured'."
                ),
            },
        ],
        "credentialRule": (
            "CLAUDE.md section 1.2, absolute: supplied in-session by environment variable only. "
            "Never a file, a log, a shell history, a config, a keychain or a default. Never "
            "echoed, never summarised back, never written into this file."
        ),
        "measurements": {
            "rebootToSshReadySeconds": 22.3,
            "rebootMeasurement": (
                "Measured twice on the mule 2026-08-15 with loss of tcp/22 confirmed in between. "
                "Every wait ceiling in the harness is a margin over this number, not an estimate "
                "of it - power.on(wait_s=120) is roughly five boots."
            ),
            "eepromPowerOffOnHalt": (
                "POWER_OFF_ON_HALT=1 is set in this Pi's EEPROM, so `halt` genuinely cuts power. A "
                "silent frame on a live relay has three explanations, not two: booting, hung, or "
                "halted and drawing nothing."
            ),
            "userSessionAfterAgentStartSeconds": 0.52,
            "userSessionMeasurement": (
                "The number behind three separate defects, measured on the mule 2026-08-16 from "
                "the frame's own persistent journal on one monotonic clock. fl-agent.service "
                "reaches active at 9.7-11.3 s into every boot; the console autologin opens its "
                "PAM session at 10.3-11.7 s, 0.52-0.89 s later, on all 30 boots the card then "
                "held; and the user manager (user@1000.service / session.slice) comes up 0.03-0.7 "
                "s after that again. The loop's first Observe is the first thing a pass does, "
                "within ~100-250 ms of the process starting, so it lands before the session "
                "exists every time. That is why boot.autologin.getty-tty1 burned five attempts "
                "and five reboots on a frame that was logging itself in correctly - its verdicts "
                "at monotonic 10.018670 and 11.188142 preceded the logins at 10.358098 and "
                "11.631500 by 352 ms and 472 ms - and it is the same instant at which every "
                "systemctl --user resource fails with 'Failed to connect to user scope bus'. "
                "Anything whose observable lives in that session must not be judged before it. "
                "Root cause in git log d275689; the gate that acts on it is 74cdedf."
            ),
            "panelTouchInput": (
                "The panel DOES expose a touch device to Linux, measured on the mule 2026-08-16, "
                "read-only and with no physical touch. /proc/bus/input/devices carries 'Goodix "
                "Capacitive TouchScreen' on Bus=0018 (i2c-11, address 0x5d, controller ID 9271) "
                "with Handlers=kbd mouse0 event4 and PROP=2, which is INPUT_PROP_DIRECT - a "
                "touchscreen rather than a touchpad. udev classifies it ID_INPUT_TOUCHSCREEN=1. "
                "The node is /dev/input/event4, crw-rw---- root:input, with the stable alias "
                "/dev/input/by-path/platform-1f00080000.i2c-event. Opened O_RDONLY|O_NONBLOCK it "
                "returns its name over EVIOCGNAME and its axes over EVIOCGABS - ABS_X 0-799 and "
                "ABS_Y 0-1279, matching the 800x1280 panel exactly, so coordinates arrive in "
                "panel pixels with no scaling - plus ABS_MT_POSITION_X/Y multitouch slots, and "
                "read() returns EAGAIN with nobody touching it. The agent runs as root and "
                "framelink is also in group 996(input). This closes a question the repository had "
                "never answered: reference/v1-state-inventory.txt has no /proc/bus/input/devices, "
                "no /dev/input listing and no evdev evidence of any kind, and the console stage's "
                "sentence 'This screen has no buttons' was false on this hardware. evtest and "
                "libinput are NOT installed on the frame, so the capability read was done with "
                "raw ioctls rather than by installing anything. Acted on in git log b6d2506."
            ),
        },
    }


def _blank() -> dict[str, Any]:
    return {
        "schema": SCHEMA,
        "generatedBy": "tools/harness/fl.py",
        "updatedUtc": utcnow(),
        "readMeFirst": list(_READ_ME_FIRST),
        "currentMilestone": None,
        "inFlight": None,
        "nextActions": [],
        "blockers": [],
        "milestones": [],
        "capabilities": {
            cap_id: {
                "title": title,
                "state": "unproven",
                "provenBy": None,
                "provenUtc": None,
                "provenCommand": command,
                "detail": None,
            }
            for cap_id, title, command in _CAPABILITIES
        },
        "resources": {},
        "orientation": dict(_ORIENTATION),
        "environment": _environment(),
        "artifacts": {
            "agentBinary": None,
            "deployed": None,
            "lastCollection": None,
        },
        "counters": {
            "builds": 0,
            "deploys": 0,
            "testRuns": 0,
            "collections": 0,
            "relayOperations": 0,
        },
        "log": [],
    }


#: Canonical key order, rewritten on every save. Ordered by what a session with no memory
#: needs first, not by what the harness happens to update most: what to do, then what is in
#: the way, then where the build stands, and the event log - the longest section by far - last.
_KEY_ORDER = [
    "schema",
    "generatedBy",
    "updatedUtc",
    "readMeFirst",
    "currentMilestone",
    "inFlight",
    "nextActions",
    "blockers",
    "milestones",
    "capabilities",
    "resources",
    "findings",
    "orientation",
    "environment",
    "artifacts",
    "counters",
    "log",
]

#: Keys carried across a schema change. Everything else in a foreign file is derived and will
#: be rebuilt on the first save, so carrying it would only preserve a stale copy of it.
_MIGRATED_KEYS = ["inFlight", "capabilities", "artifacts", "counters", "blockers", "log"]


def load() -> dict[str, Any]:
    """Read the progress file, creating a blank one if it does not exist yet.

    A file written under a different schema is **migrated**, not stepped around: the earned
    state - capabilities, artifacts, counters, blockers, the log - is carried across and
    everything derived is rebuilt on the next save. The previous behaviour moved the whole
    file under ``previousSchema`` and started blank, which meant the one moment the format
    changed was also the moment the record of what had been proven stopped being read.
    """
    if not PROGRESS_FILE.exists():
        return _blank()
    try:
        data = json.loads(PROGRESS_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fresh = _blank()
        fresh["blockers"] = [
            {
                "id": "progress-file-unreadable",
                "what": f"The previous progress file could not be parsed: {exc}",
                "needs": "Nothing - a blank file has taken over. Prior history is lost.",
                "since": utcnow(),
            }
        ]
        return fresh

    if data.get("schema") != SCHEMA:
        fresh = _blank()
        for key in _MIGRATED_KEYS:
            if key in data:
                fresh[key] = data[key]
        fresh["migratedFrom"] = {"schema": data.get("schema"), "utc": utcnow()}
        data = fresh

    # Tolerate a file written by an older run that predates a capability being added.
    for cap_id, title, command in _CAPABILITIES:
        data.setdefault("capabilities", {}).setdefault(
            cap_id,
            {
                "title": title,
                "state": "unproven",
                "provenBy": None,
                "provenUtc": None,
                "provenCommand": command,
                "detail": None,
            },
        )
    return data


def save(data: dict[str, Any]) -> None:
    """Atomically write the progress file.

    Temp file in the same directory then ``os.replace``, which is atomic on both NTFS and
    ext4. A half-written progress file would be worse than none at all, because a resuming
    session would trust it.
    """
    data["updatedUtc"] = utcnow()
    data["log"] = data.get("log", [])[-LOG_LIMIT:]
    # Derived on every write, never authored. Anything that could be set by hand would
    # eventually disagree with the state beneath it, and the disagreement would be invisible.
    _refresh(data)

    PROGRESS_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = PROGRESS_FILE.with_suffix(".json.tmp")
    # newline="\n" because .gitattributes pins the whole repository to LF in the working
    # tree on every OS, and this file is tracked. Without it Python would translate to
    # CRLF on Windows and every harness run would show as a whole-file diff.
    tmp.write_text(
        json.dumps(_ordered(data), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    os.replace(tmp, PROGRESS_FILE)


def _ordered(data: dict[str, Any]) -> dict[str, Any]:
    """Rebuild the mapping in canonical key order, keeping anything unrecognised at the end.

    Python preserves insertion order, so without this a key added by a later revision lands
    wherever it was first written - which for an existing file is the very bottom, under a
    two-hundred-entry log. Rewriting the order on every save is what keeps the diff of this
    file about its content.
    """
    ordered = {key: data[key] for key in _KEY_ORDER if key in data}
    ordered.update({key: value for key, value in data.items() if key not in ordered})
    return ordered


def prove(cap_id: str, *, by: str, detail: str) -> None:
    """Mark a capability proven. Called only from the code path that actually proved it."""
    data = load()
    cap = data["capabilities"].setdefault(cap_id, {"title": cap_id})
    cap["state"] = "proven"
    cap["provenBy"] = by
    cap["provenUtc"] = utcnow()
    cap["detail"] = detail
    # The command just ran, so whatever previously made it unrunnable no longer does. Without
    # this the note outlives its cause: `fl.py status` goes on printing "not runnable in this
    # session" under a capability that was re-proven minutes ago, and a resuming session has no
    # way to tell that obstacle from a current one.
    cap.pop("currentlyUnrunnable", None)
    save(data)


def mark(cap_id: str, state: str, *, detail: str) -> None:
    """Record a non-proven capability state: ``blocked``, ``failed`` or back to ``unproven``.

    A capability that was proven earlier is not demoted by a later failure of an unrelated
    kind; it is demoted only by ``failed``, which means the proving command ran and lost.
    """
    data = load()
    cap = data["capabilities"].setdefault(cap_id, {"title": cap_id})
    if state == "blocked" and cap.get("state") == "proven":
        # Already proven once. Being unable to re-run it right now (no FL_PW in this
        # session, say) is not evidence against the earlier proof, so the proof stands and
        # the obstruction is noted beside it. Stored as its own field rather than appended
        # to the detail string: appending grew without bound across repeated runs, and the
        # tenth copy of the same note is not ten times the information.
        cap["currentlyUnrunnable"] = detail
    else:
        cap["state"] = state
        cap["detail"] = detail
        cap.pop("currentlyUnrunnable", None)
        if state == "failed":
            cap["provenBy"] = None
            cap["provenUtc"] = None
    save(data)


def add_blocker(blocker_id: str, what: str, needs: str) -> None:
    """Record something the harness cannot do, and what it would take. Idempotent by id."""
    data = load()
    blockers = [b for b in data.get("blockers", []) if b.get("id") != blocker_id]
    blockers.append({"id": blocker_id, "what": what, "needs": needs, "since": utcnow()})
    data["blockers"] = blockers
    save(data)


def clear_blocker(blocker_id: str) -> None:
    """Remove a blocker that no longer applies."""
    data = load()
    before = data.get("blockers", [])
    after = [b for b in before if b.get("id") != blocker_id]
    if len(after) != len(before):
        data["blockers"] = after
        save(data)


def set_artifact(name: str, value: dict[str, Any] | None) -> None:
    """Record a produced artifact - the built binary, what is deployed, last collection."""
    data = load()
    data.setdefault("artifacts", {})[name] = value
    save(data)


def bump(counter: str, amount: int = 1) -> int:
    """Increment a counter and return the new value.

    ``relayOperations`` is the one that matters most: it makes hardware wear a visible,
    accumulating number rather than something nobody notices until 350 operations later.
    """
    data = load()
    counters = data.setdefault("counters", {})
    counters[counter] = counters.get(counter, 0) + amount
    total = counters[counter]
    save(data)
    return total


def set_next_actions(actions: list[str]) -> None:
    """Replace the list of what a resuming session should do next."""
    data = load()
    data["nextActions"] = actions
    save(data)


def log(command: str, ok: bool, summary: str, **extra: Any) -> None:
    """Append one bounded event to the log."""
    data = load()
    entry: dict[str, Any] = {"utc": utcnow(), "command": command, "ok": ok, "summary": summary}
    entry.update(extra)
    data.setdefault("log", []).append(entry)
    save(data)


@contextmanager
def activity(command: str, **context: Any):
    """Bracket a subcommand with an ``inFlight`` marker.

    Written before the work starts and cleared after it ends, so a session killed
    mid-command leaves the marker behind. That marker is the only way a resuming session
    can tell "this never ran" apart from "this ran and its result is unknown" - which
    matters most for `deploy` and `power`, the two subcommands that change something
    outside this repository.
    """
    data = load()
    data["inFlight"] = {"command": command, "startedUtc": utcnow(), **context}
    save(data)
    try:
        yield
    finally:
        data = load()
        data["inFlight"] = None
        # A marker that was written, survived a whole subcommand and was then cleared IS
        # the demonstration that the resume mechanism works. Proving it here rather than
        # asserting it in the seed data keeps the "states are earned" rule intact for the
        # one capability that would otherwise have nothing to earn it.
        capability = data["capabilities"].setdefault("progress-file", {"title": "progress-file"})
        if capability.get("state") != "proven":
            capability["state"] = "proven"
            capability["provenBy"] = f"fl.py {command}"
            capability["provenUtc"] = utcnow()
            capability["detail"] = (
                f"inFlight marker written, survived `{command}`, and was cleared; "
                f"file written atomically to {PROGRESS_FILE.name}"
            )
        save(data)


def _milestone_state_from_capabilities(data: dict[str, Any]) -> str:
    """M0's state, derived from the seven capabilities and from nothing else."""
    states = [cap.get("state") for cap in data.get("capabilities", {}).values()]
    if states and all(state == "proven" for state in states):
        return "done"
    if any(state == "failed" for state in states):
        return "failing"
    if any(state == "proven" for state in states):
        return "in-progress"
    return "not-started"


def _milestone_state_from_resources(ledger: dict[str, Any]) -> tuple[str, dict[str, Any]]:
    """M3's state, derived from the measured ledger. Returns the state and its evidence."""
    total = ledger.get("catalogTotal")
    implemented = ledger.get("implemented")
    verified = ledger.get("hardwareVerifiedCount", 0)

    if implemented is None or total is None:
        return "unknown", {
            "how": "derived-from-the-repository",
            "what": (
                "The counts this state is derived from could not be read - see "
                "resources.catalogTotalReadFrom and resources.implementedReadFrom."
            ),
        }

    if implemented >= total and verified >= total:
        state = "done"
    else:
        state = "in-progress" if implemented else "not-started"

    return state, {
        "how": "derived-from-the-repository",
        "what": (
            (
                f"The agent's graph carries {implemented} resources against the catalog's "
                f"{total}, {implemented - total} net beyond it; "
                if implemented > total
                else f"{implemented} of {total} catalog resources are implemented in the agent; "
            )
            + f"{verified} of {implemented} can still be shown to have been reboot-verified on "
            "real hardware, which is a floor and not a census. Both numbers are measured on "
            "every write, not recorded."
        ),
        "notWitnessed": (
            "The triple bar this milestone is graded on - state-diff against the frozen v1 "
            "reference, checkpoint assertions, validation battery on the mule - has not been run "
            "for any group. The state-diff harness now exists (`fl.py parity`, judged by "
            "tools/FrameLink.Parity) and is tested against fixtures, but it has never been run "
            "against a frame: no observation has ever been collected and no parity verdict has "
            "ever been issued. The other two bars have no harness."
        ),
    }


def _refresh(data: dict[str, Any]) -> None:
    """Rebuild every derived section. Never authored, never hand-edited.

    Constants that belong to the code rather than to accumulated state - the milestone ladder,
    the orientation pointers, the environment contract, the read-me - are rewritten from the
    templates above on every save. Otherwise a file created weeks ago keeps quoting a spec line
    that has since been reworded, and the record slowly stops describing the thing it records.
    """
    data["schema"] = SCHEMA
    data["generatedBy"] = "tools/harness/fl.py"
    data["readMeFirst"] = list(_READ_ME_FIRST)
    data["findings"] = [dict(finding) for finding in _HARDWARE_FINDINGS]
    data["orientation"] = dict(_ORIENTATION)
    data["environment"] = _environment()

    ledger = _resource_ledger()
    data["resources"] = ledger

    milestones: list[dict[str, Any]] = []
    for template in _MILESTONES:
        milestone = {
            "id": template["id"],
            "title": template["title"],
            "doneWhen": template["doneWhen"],
            "specRef": template["specRef"],
            "state": "not-started",
            "stateFrom": template["stateFrom"],
        }
        if template["stateFrom"] == "capabilities":
            milestone["state"] = _milestone_state_from_capabilities(data)
            milestone["evidence"] = {
                "how": "earned-by-harness-commands",
                "what": (
                    "Every one of the seven capabilities below was proven by the fl.py subcommand "
                    "that performs it. Nothing here was recorded by hand."
                ),
            }
        elif template["stateFrom"] == "resources":
            milestone["state"], milestone["evidence"] = _milestone_state_from_resources(ledger)
        elif template["stateFrom"] == "witness":
            milestone["state"] = template["state"]
            milestone["evidence"] = dict(template["evidence"])
        milestones.append(milestone)

    data["milestones"] = milestones
    data["currentMilestone"] = next(
        (m["id"] for m in milestones if m["state"] != "done"),
        milestones[-1]["id"] if milestones else None,
    )

    # The singular `milestone` key was this file's whole vocabulary when M0 was the whole
    # world. It is gone, and it is popped rather than merely not written because an older
    # copy of this module still running in another process re-creates it at its own next
    # save. Leaving it would put a stale M0 header above a ladder that disagrees with it.
    data.pop("milestone", None)
