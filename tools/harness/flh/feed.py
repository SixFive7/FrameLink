"""What the Fleet Manager is actually serving, read from the Fleet Manager itself.

version2.md section 2.8 makes the served build authoritative over the installed one: the
agent checks its Fleet Manager every hour, out of band, and **matches** the served version
rather than taking the greater of the two. Upgrade or downgrade, always. The handshake does
the same thing sooner - it is an optimisation, not a mechanism - so a frame that reconnects
converges in seconds rather than within the hour.

That is the property this module exists to make visible. Without it ``fl.py deploy`` is a
command that installs a binary, verifies it by the mule's own sha256sum, prints a confident
report, and is silently undone before the report finishes scrolling. Measured 2026-08-23: a
deploy of ``0.0.0+ebc474a.dirty`` onto a frame whose Fleet Manager served
``0.0.0+0384c01.dirty`` was reverted in **six seconds**, the agent's own journal narrating it
as ``Converging from 0.0.0+ebc474a.dirty to the served version 0.0.0+0384c01.dirty``. A
deploy that holds and a deploy that will be erased produced identical harness output.

The route is deliberately the one an agent uses. ``GET /agent/release/<rid>`` is
unauthenticated on purpose (AgentEndpoints: "an agent whose protocol is too old to be adopted
must still be able to repair itself"), versionless, and outside the negotiated protocol - so
reading it needs no operator password, no session and no cooperation from the GUI, and what
it returns is by construction the same bytes the fleet is converging on.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any

from .config import CONTROL_URL, RID, HarnessError

#: Route shape. Server-relative and versionless, matching section 4.2.
RELEASE_PATH = "/agent/release/{rid}"


def release_url(rid: str = RID, *, base_url: str = CONTROL_URL) -> str:
    """The address the metadata for one runtime identifier is read from."""
    return f"{base_url.rstrip('/')}{RELEASE_PATH.format(rid=rid)}"


def served_release(rid: str = RID, *, base_url: str = CONTROL_URL, timeout: float = 10.0) -> dict[str, Any]:
    """The ``AgentRelease`` this Fleet Manager serves for ``rid``.

    Raises :class:`HarnessError` rather than returning ``None`` on every failure, so that a
    caller which wants to continue anyway has to say so explicitly. Silence about an
    unreachable feed is the same defect as silence about a mismatched one.
    """
    url = release_url(rid, base_url=base_url)
    request = urllib.request.Request(url, method="GET")  # noqa: S310 - fixed http(s) base
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            payload = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:300]
        if exc.code == 404:
            # AgentReleaseCatalog answers 404 for "nothing published for that runtime", which
            # is exactly the state an image built from a checkout that never ran `fl.py build`
            # is in. Worth its own sentence, because it is a normal state rather than a fault.
            raise HarnessError(
                f"{base_url} serves no agent build for {rid}: {detail}",
                exit_code=10,
                remedy=(
                    "The Fleet Manager's release directory is empty for this runtime. If it is a "
                    "container, it was built from a checkout with no build/out - rebuild it with "
                    "deploy/fleet-manager/build-image.sh after `fl.py build`."
                ),
            ) from exc
        raise HarnessError(
            f"{url} returned HTTP {exc.code}: {detail}",
            exit_code=5,
        ) from exc
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise HarnessError(
            f"Cannot reach the Fleet Manager's update feed at {url}: {exc}",
            exit_code=3,
            remedy=(
                "Set FL_CONTROL_URL if the Fleet Manager moved. The default is the LAN address "
                "the frame itself dials, not loopback, so that what this reads is what the fleet "
                "converges on."
            ),
        ) from exc

    try:
        release = json.loads(payload)
    except json.JSONDecodeError as exc:
        raise HarnessError(f"{url} did not answer JSON: {payload[:200]!r}", exit_code=5) from exc

    if not isinstance(release, dict) or "version" not in release or "sha256" not in release:
        raise HarnessError(
            f"{url} answered JSON that is not an AgentRelease: {payload[:200]!r}",
            exit_code=5,
        )
    return release


def compare(local: dict[str, Any], local_sha: str, served: dict[str, Any]) -> tuple[bool, str]:
    """Whether the served feed is the build being deployed, and one line saying why.

    Both halves are compared, and the two ways they can disagree are not the same fault.
    A different **version** is the ordinary case - somebody built an agent and did not
    rebuild the Fleet Manager that serves it. Identical version strings over different
    **bytes** is the pathological one: section 2.8's updater matches version strings and
    never compares them, so a frame would download, install, restart, find itself reporting
    the version it already advertised, and do it again on the next tick - for ever.
    """
    same_version = str(served.get("version", "")) == str(local.get("version", ""))
    same_bytes = str(served.get("sha256", "")).lower() == local_sha.lower()

    if same_version and same_bytes:
        return True, f"the feed serves {served['version']} and it is these exact bytes"
    if same_version and not same_bytes:
        return False, (
            f"the feed serves version {served['version']}, the same string this build "
            f"advertises, over DIFFERENT bytes (feed {str(served.get('sha256', ''))[:12]}..., "
            f"built {local_sha[:12]}...). Section 2.8's updater matches version strings and "
            "never compares them, so a frame cannot converge out of this state on its own."
        )
    # Phrased without a verb for what the caller is about to do with the local build, because
    # it has two callers that would need different ones: `deploy` is about to install it, and
    # `status` is not about to do anything at all.
    return False, (
        f"the feed serves {served['version']}, build/out holds {local['version']}"
    )
