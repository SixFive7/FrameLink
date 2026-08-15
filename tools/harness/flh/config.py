"""Paths, hosts and environment contract for the FrameLink autonomy harness.

Everything the harness needs to know about *where* things are lives here, so no other
module hard-codes a host, a path or a variable name.

Environment contract
--------------------
Required, and never defaulted:

    FL_PW           SSH password for the mule.

                    CLAUDE.md section 1.2 is binding and absolute: the password is supplied
                    per-session through this variable only. It is never written to a file,
                    a log, a shell history, a config, a keychain or a default value, and
                    there is no key-based fallback. Every mule-touching subcommand checks
                    for it up front and fails with a named error rather than hanging on a
                    password prompt or silently trying another credential.

    FL_HA_TOKEN     Home Assistant long-lived access token, for `fl.py power`.
                    Same rule: environment only, never persisted.

Optional, with defaults that match the current bench setup:

    FL_HOST         mule address                (default 10.20.30.53)
    FL_USER         mule username               (default framelink)
    FL_HA_URL       Home Assistant base URL     (default http://10.20.30.250:8123)
    FL_HA_ENTITY    smart plug entity id        (default switch.wall_plug_25)
"""

from __future__ import annotations

import os
from pathlib import Path

# --- repository layout -----------------------------------------------------
# config.py lives at tools/harness/flh/config.py, so the repo root is three up.
HARNESS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = HARNESS_DIR.parent.parent

BUILD_DIR = REPO_ROOT / "build"
BUILD_OUT = BUILD_DIR / "out"
BUILD_SCRIPT_IN_CONTAINER = "/src/build/build.sh"
DOCKERFILE = BUILD_DIR / "Dockerfile"

# Not gitignored. This is the record that lets a fresh session with no memory resume
# mid-milestone (version2.md section 5.5) - .gitignore covers tools/harness/runs/ only.
PROGRESS_FILE = HARNESS_DIR / "progress.json"

# Gitignored. Screenshots, journal captures, build logs, relay wear ledger.
RUNS_DIR = HARNESS_DIR / "runs"

UNIT_TEMPLATE = HARNESS_DIR / "assets" / "fl-agent.service"

# --- build -----------------------------------------------------------------
AGENT_PROJECT = "src/FrameLink.Agent"
RID = "linux-arm64"
BINARY_NAME = "fl-agent"
BUILD_IMAGE = "framelink-build:arm64"
STOCK_SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:10.0"
BUILD_PLATFORM = "linux/arm64"

# Named volume for the container's NuGet cache. Without it every emulated build
# re-downloads the whole restore graph under QEMU, which is the slowest part of the loop.
NUGET_CACHE_VOLUME = "framelink-nuget"

# --- test ------------------------------------------------------------------
TEST_PROJECT = "tests/FrameLink.Tests"

# --- mule ------------------------------------------------------------------
MULE_HOST = os.environ.get("FL_HOST", "10.20.30.53")
MULE_USER = os.environ.get("FL_USER", "framelink")
MULE_SSH_PORT = 22

# Measured on the mule 2026-08-15, and recorded because several waits below and in power.py
# are set against them rather than guessed:
#
#   * A full reboot to SSH-ready is **22.3 s**, taken twice (22.3 s and ~20 s) with loss of
#     port 22 confirmed in between. Every wait ceiling here is a margin over that number, not
#     an estimate of it - `power.on(wait_s=120)` is roughly five boots, `power.cycle(
#     wait_s=180)` roughly eight. Anything that starts reading them as the cost of a boot will
#     budget a provision several times too pessimistically.
#   * **POWER_OFF_ON_HALT=1** is set in this Pi's EEPROM, so `halt` and `poweroff` genuinely
#     cut power rather than leaving the board idling. A silent frame on a live relay therefore
#     has three explanations, not two: booting, hung, or halted and drawing nothing.
REMOTE_STAGE = "/tmp/fl-agent.staged"
REMOTE_BIN = "/usr/local/bin/fl-agent"
REMOTE_UNIT = "/etc/systemd/system/fl-agent.service"
UNIT_NAME = "fl-agent.service"

# The unit is written to /tmp as the login user and then installed into place as root.
# Writing it directly with `sudo tee` would put the heredoc on sudo's stdin, which is
# exactly where the password has to go on an image with no NOPASSWD rule (ssh.py).
REMOTE_UNIT_STAGE = "/tmp/fl-agent.service.staged"

# --- home assistant --------------------------------------------------------
# 8123 is Home Assistant's port, verified against the live instance 2026-08-15: GET /api/
# answers {"message":"API running."}. The previous default of 8086 was a different service on
# the same host (an MCP server) which answers HTTP 404 for *every* path, including /api/ - so
# a state lookup came back 404 and read exactly like "that entity does not exist" while the
# entity was fine and drawing 3.54 W. HA_API_PROBE below is what tells those two apart now.
HA_URL = os.environ.get("FL_HA_URL", "http://10.20.30.250:8123").rstrip("/")
HA_ENTITY = os.environ.get("FL_HA_ENTITY", "switch.wall_plug_25")

#: The cheapest question that distinguishes "wrong entity" from "not a Home Assistant".
#: Home Assistant answers 200 with {"message": "API running."}; anything else answering on
#: this URL almost certainly does not.
HA_API_PROBE = "/api/"

# --- relay safety ----------------------------------------------------------
# A previous session wore roughly 350 relay operations before any of these guards existed
# and the user had to intervene. Hardware wear is a real cost; these are the cheapest
# possible defences and they are on by default.

#: Minimum seconds between any two relay operations. Rapid-cycling a mechanical relay is
#: the fastest way to wear its contacts, and a Pi needs this long to shed capacitance
#: anyway for the power cut to actually be a power cut.
RELAY_MIN_INTERVAL_S = 30.0

#: Refuse to exceed this many relay operations in one rolling hour without --i-accept-wear.
RELAY_MAX_PER_HOUR = 20

#: After switching the relay off, the frame must stop answering within this many seconds.
#: If it is still answering, the relay that moved is not the one feeding the frame - the
#: wrong entity is being switched, and something else in the house just lost power.
RELAY_OFF_LIVENESS_DEADLINE_S = 20.0

#: How long to wait for Home Assistant to report the new relay state before calling the
#: switch unconfirmed.
RELAY_STATE_CONFIRM_TIMEOUT_S = 15.0

#: Seconds the frame stays unpowered during `power cycle`. Long enough to drain, short
#: enough not to waste a session.
RELAY_CYCLE_OFF_S = 10.0

#: Persisted wear ledger. Under runs/ (gitignored) because it is local bench state, not a
#: fact about the repository.
RELAY_LEDGER = RUNS_DIR / "relay-ledger.json"


class HarnessError(RuntimeError):
    """A failure the harness diagnosed itself.

    Raised instead of letting a library exception escape, so `fl.py` can print one clear
    line and set a meaningful exit code rather than a traceback.
    """

    def __init__(self, message: str, *, exit_code: int = 1, remedy: str | None = None):
        super().__init__(message)
        self.exit_code = exit_code
        self.remedy = remedy


class ElevationError(HarnessError):
    """sudo on the mule refused, or cannot be used by this login at all.

    Its own type because callers that deliberately absorb a failed step - `collect` tries a
    second screenshot path when the first one fails - must not absorb this one. An image the
    harness cannot elevate on fails every root-shaped step for the same reason, so folding it
    into a per-step failure list would bury the one sentence that explains all of them and
    replace it with a remedy about the wrong subject entirely.
    """


def require_password() -> str:
    """Return FL_PW, or raise the one error every mule-touching command must give.

    Never logged, never echoed, never written anywhere. Callers pass the returned value
    straight into paramiko's ``password=`` argument and hold no other reference.
    """
    pw = os.environ.get("FL_PW")
    if not pw:
        raise HarnessError(
            "FL_PW is not set - cannot reach the mule.",
            exit_code=2,
            remedy=(
                "Supply the mule password for this session only, inline with the command:\n"
                "    FL_PW='...' python tools/harness/fl.py <subcommand>       (bash)\n"
                '    $env:FL_PW=\'...\'; python tools/harness/fl.py <subcommand>  (PowerShell)\n'
                "The harness never stores it, and there is deliberately no key-based fallback."
            ),
        )
    return pw


def require_ha_token() -> str:
    """Return FL_HA_TOKEN, or explain how to get one."""
    token = os.environ.get("FL_HA_TOKEN")
    if not token:
        raise HarnessError(
            "FL_HA_TOKEN is not set - cannot control the smart plug.",
            exit_code=2,
            remedy=(
                f"Create a long-lived access token in Home Assistant at {HA_URL}\n"
                "(profile page, bottom of the Security tab) and supply it inline:\n"
                "    FL_HA_TOKEN='...' python tools/harness/fl.py power state\n"
                "Optionally override FL_HA_URL and FL_HA_ENTITY the same way."
            ),
        )
    return token
