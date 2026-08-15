"""Smart-plug control for the mule, through Home Assistant.

No Home Assistant tooling is exposed to this harness (checked: the deferred-tool set holds
no HA integration), so this speaks the Home Assistant REST API directly over ``urllib``
from the standard library - two endpoints, no dependency worth adding for them.

    GET  {FL_HA_URL}/api/states/{entity}          -> current relay state
    POST {FL_HA_URL}/api/services/switch/turn_on  -> {"entity_id": entity}
    POST {FL_HA_URL}/api/services/switch/turn_off

Safety rules, carried over from v1
----------------------------------
A previous session performed roughly 350 relay operations before any of these existed and
the user had to step in and stop it. Relay contacts are consumable and a Pi's SD card does
not enjoy hard cuts either, so the guards are on by default and the defaults are the
fewest operations that do the job:

1. **Never rapid-cycle.** A persisted ledger enforces a minimum interval between any two
   operations and a ceiling per rolling hour. The ledger survives across sessions, because
   the relay does not care which session wore it.
2. **Already in the requested state is success, not a reason to switch.** ``power off`` on
   an already-dark frame performs no relay operation at all.
3. **Confirm the relay actually moved.** Home Assistant is asked to report the new state
   back, and a switch that never confirms is a failure, not a silent success.
4. **Abort loudly if the frame is still alive seconds after relay-off.** That combination
   means the relay that moved is not the one feeding the frame - the wrong entity is being
   switched, and something else in the house just lost power. The harness turns the relay
   straight back on and stops. This is the check whose absence made the 350-operation run
   possible.

Liveness is tested two ways and *either* counts as alive: an ICMP echo with a real TTL,
and a TCP connection to the SSH port. Using the stricter union deliberately biases the
wrong-entity check towards false alarms rather than towards silently cutting the wrong
circuit.
"""

from __future__ import annotations

import json
import platform
import subprocess
import time
import urllib.error
import urllib.request
from typing import Any

from . import progress, ssh, ui
from .config import (
    HA_ENTITY,
    HA_URL,
    MULE_HOST,
    RELAY_CYCLE_OFF_S,
    RELAY_LEDGER,
    RELAY_MAX_PER_HOUR,
    RELAY_MIN_INTERVAL_S,
    RELAY_OFF_LIVENESS_DEADLINE_S,
    RELAY_STATE_CONFIRM_TIMEOUT_S,
    RUNS_DIR,
    HarnessError,
    require_ha_token,
)


# --------------------------------------------------------------------------
# Home Assistant
# --------------------------------------------------------------------------
def _request(path: str, *, method: str = "GET", body: dict[str, Any] | None = None) -> Any:
    token = require_ha_token()
    url = f"{HA_URL}{path}"
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)  # noqa: S310 - fixed http(s) base
    request.add_header("Authorization", f"Bearer {token}")
    request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=20) as response:  # noqa: S310
            payload = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:400]
        remedy = None
        if exc.code == 401:
            remedy = "FL_HA_TOKEN is set but rejected - the long-lived token is wrong or revoked."
        elif exc.code == 404:
            remedy = f"Home Assistant does not know {HA_ENTITY!r}. Override with FL_HA_ENTITY."
        raise HarnessError(f"Home Assistant returned HTTP {exc.code} for {method} {path}: {detail}",
                           exit_code=5, remedy=remedy) from exc
    except urllib.error.URLError as exc:
        raise HarnessError(
            f"Cannot reach Home Assistant at {HA_URL}: {exc.reason}",
            exit_code=3,
            remedy="Override the base URL with FL_HA_URL if the server moved.",
        ) from exc
    return json.loads(payload) if payload.strip() else None


def relay_state() -> str:
    """Current relay state as Home Assistant reports it: ``on``, ``off`` or ``unavailable``."""
    state = _request(f"/api/states/{HA_ENTITY}")
    return str(state.get("state", "unknown"))


# --------------------------------------------------------------------------
# Liveness
# --------------------------------------------------------------------------
def _icmp_alive(host: str, *, timeout_ms: int = 1000) -> bool:
    """One ICMP echo. True only on a reply carrying a TTL.

    Windows' ``ping`` exits 0 even when what came back was an ICMP error such as
    "Destination host unreachable", so the exit code alone would report a dead frame as
    alive - the exact direction of error the wrong-entity check must not make. Matching on
    ``TTL=`` is the reliable signal on both platforms.
    """
    if platform.system() == "Windows":
        argv = ["ping", "-n", "1", "-w", str(timeout_ms), host]
    else:
        argv = ["ping", "-c", "1", "-W", str(max(1, timeout_ms // 1000)), host]
    try:
        result = subprocess.run(  # noqa: S603
            argv, capture_output=True, text=True, timeout=timeout_ms / 1000 + 5, check=False
        )
    except (OSError, subprocess.SubprocessError):
        return False
    return "ttl=" in result.stdout.lower()


def frame_alive(host: str = MULE_HOST) -> tuple[bool, str]:
    """Is the frame answering? Returns ``(alive, how)``.

    Either signal counts. The union is deliberate: a frame that answers ICMP but not SSH is
    still powered, and powered is the whole question here.
    """
    if _icmp_alive(host):
        return True, "icmp"
    if ssh.is_alive(host):
        return True, "tcp/22"
    return False, "silent"


# --------------------------------------------------------------------------
# Wear ledger
# --------------------------------------------------------------------------
def _load_ledger() -> dict[str, Any]:
    if not RELAY_LEDGER.is_file():
        return {"operations": []}
    try:
        return json.loads(RELAY_LEDGER.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"operations": []}


def _record_operation(action: str) -> int:
    """Append to the ledger *before* the relay moves, and return the lifetime total.

    Recorded first on purpose: a session killed between the API call and the confirmation
    still leaves the wear counted. Under-counting wear is the failure mode that matters.
    """
    RUNS_DIR.mkdir(parents=True, exist_ok=True)
    ledger = _load_ledger()
    ledger.setdefault("operations", []).append(
        {"utc": progress.utcnow(), "epoch": time.time(), "action": action, "entity": HA_ENTITY}
    )
    ledger["lifetimeOperations"] = len(ledger["operations"])
    RELAY_LEDGER.write_text(json.dumps(ledger, indent=2) + "\n", encoding="utf-8")
    progress.bump("relayOperations")
    return ledger["lifetimeOperations"]


def _check_rate_limits(*, accept_wear: bool) -> None:
    ledger = _load_ledger()
    operations = ledger.get("operations", [])
    now = time.time()

    if operations:
        last = operations[-1]
        gap = now - float(last.get("epoch", 0))
        if gap < RELAY_MIN_INTERVAL_S:
            raise HarnessError(
                f"Rapid-cycle guard: last relay operation was {gap:.0f}s ago, minimum is "
                f"{RELAY_MIN_INTERVAL_S:.0f}s.",
                exit_code=7,
                remedy=(
                    f"Wait {RELAY_MIN_INTERVAL_S - gap:.0f}s. The interval protects the relay "
                    "contacts and gives the Pi time to actually lose power."
                ),
            )

    recent = [op for op in operations if now - float(op.get("epoch", 0)) < 3600]
    if len(recent) >= RELAY_MAX_PER_HOUR and not accept_wear:
        raise HarnessError(
            f"Wear guard: {len(recent)} relay operations in the last hour, ceiling is "
            f"{RELAY_MAX_PER_HOUR}.",
            exit_code=7,
            remedy=(
                "Every one of these is mechanical wear on a real relay. If the run genuinely "
                "needs more, pass --i-accept-wear and say why in --reason."
            ),
        )


# --------------------------------------------------------------------------
# Operations
# --------------------------------------------------------------------------
def _call_switch(action: str) -> None:
    _request(f"/api/services/switch/turn_{action}", method="POST", body={"entity_id": HA_ENTITY})


def _confirm_state(expected: str) -> str:
    """Poll Home Assistant until the relay reports ``expected``, or give up.

    A service call that returns 200 only means the call was accepted. What matters is
    whether the relay moved, and only the state endpoint answers that.
    """
    deadline = time.time() + RELAY_STATE_CONFIRM_TIMEOUT_S
    observed = "unknown"
    while time.time() < deadline:
        observed = relay_state()
        if observed == expected:
            return observed
        time.sleep(1.0)
    raise HarnessError(
        f"Relay did not confirm {expected!r} within {RELAY_STATE_CONFIRM_TIMEOUT_S:.0f}s "
        f"(Home Assistant still reports {observed!r}).",
        exit_code=8,
        remedy=(
            f"The switch may be unreachable rather than unresponsive. Check {HA_ENTITY} in "
            "Home Assistant before switching again."
        ),
    )


def off(*, reason: str = "", accept_wear: bool = False, skip_liveness_check: bool = False) -> dict[str, Any]:
    """Cut power to the frame, with the wrong-entity guard."""
    _check_rate_limits(accept_wear=accept_wear)

    current = relay_state()
    if current == "off":
        ui.ok(f"{HA_ENTITY} is already off - no relay operation performed")
        return {"action": "off", "relayOperations": 0, "state": "off", "wrongEntity": False}

    alive_before, how_before = frame_alive()
    ui.info(f"before: relay={current} frame={'alive (' + how_before + ')' if alive_before else 'silent'}")

    total = _record_operation("off")
    ui.step(f"Switching {HA_ENTITY} OFF (lifetime relay operations: {total}){f' - {reason}' if reason else ''}")
    _call_switch("off")
    state = _confirm_state("off")
    ui.ok(f"Home Assistant confirms {HA_ENTITY} = {state}")

    result: dict[str, Any] = {
        "action": "off",
        "relayOperations": 1,
        "state": state,
        "wrongEntity": False,
        "livenessCheck": "performed",
    }

    if skip_liveness_check:
        ui.warn("wrong-entity liveness check skipped by request")
        result["livenessCheck"] = "skipped"
        return result

    if not alive_before:
        # The frame was already silent, so "still answering" can never become true and the
        # check proves nothing. Saying so is the honest outcome; claiming a pass would be
        # exactly the false confidence the guard exists to prevent.
        ui.warn(
            "wrong-entity check inconclusive: the frame was already silent before the cut, "
            "so its silence afterwards is not evidence the right relay moved"
        )
        result["livenessCheck"] = "inconclusive (frame already silent)"
        return result

    ui.step(f"Confirming the frame goes dark within {RELAY_OFF_LIVENESS_DEADLINE_S:.0f}s")
    deadline = time.time() + RELAY_OFF_LIVENESS_DEADLINE_S
    while time.time() < deadline:
        alive, how = frame_alive()
        if not alive:
            elapsed = RELAY_OFF_LIVENESS_DEADLINE_S - (deadline - time.time())
            ui.ok(f"frame went silent after {elapsed:.0f}s - the right relay moved")
            result["darkAfterSeconds"] = round(elapsed, 1)
            return result
        time.sleep(1.5)

    # Still answering. The relay that moved is not the one feeding this frame.
    alive, how = frame_alive()
    result["wrongEntity"] = True
    ui.abort(
        f"WRONG ENTITY: {HA_ENTITY} reports off, but {MULE_HOST} is still answering ({how})\n"
        f"{RELAY_OFF_LIVENESS_DEADLINE_S:.0f}s after the cut.\n"
        "Something ELSE in the house just lost power. Restoring the relay and stopping.\n"
        "Do not re-run power commands until FL_HA_ENTITY names the plug that actually feeds the frame."
    )
    try:
        _record_operation("on(recovery)")
        _call_switch("on")
        _confirm_state("on")
        ui.warn(f"{HA_ENTITY} switched back on - whatever it feeds has power again")
        result["recovered"] = True
        result["relayOperations"] = 2
    except HarnessError as exc:
        ui.fail(f"Recovery switch-on FAILED: {exc}. {HA_ENTITY} is off and needs manual attention.")
        result["recovered"] = False

    progress.add_blocker(
        "relay-wrong-entity",
        f"{HA_ENTITY} switched off but {MULE_HOST} stayed alive - the entity does not feed the frame.",
        "Identify the correct Home Assistant switch entity and set FL_HA_ENTITY to it.",
    )
    raise HarnessError(
        f"Aborted: {HA_ENTITY} is not the relay feeding {MULE_HOST}.",
        exit_code=9,
        remedy="Set FL_HA_ENTITY to the correct entity before any further power command.",
    )


def on(*, reason: str = "", accept_wear: bool = False, wait_s: float = 120.0) -> dict[str, Any]:
    """Restore power and, optionally, wait for the frame to answer again."""
    _check_rate_limits(accept_wear=accept_wear)

    current = relay_state()
    if current == "on":
        ui.ok(f"{HA_ENTITY} is already on - no relay operation performed")
        result: dict[str, Any] = {"action": "on", "relayOperations": 0, "state": "on"}
    else:
        total = _record_operation("on")
        ui.step(f"Switching {HA_ENTITY} ON (lifetime relay operations: {total}){f' - {reason}' if reason else ''}")
        _call_switch("on")
        state = _confirm_state("on")
        ui.ok(f"Home Assistant confirms {HA_ENTITY} = {state}")
        result = {"action": "on", "relayOperations": 1, "state": state}

    if wait_s > 0:
        ui.step(f"Waiting up to {wait_s:.0f}s for {MULE_HOST} to answer")
        started = time.time()
        while time.time() - started < wait_s:
            alive, how = frame_alive()
            if alive:
                elapsed = time.time() - started
                ui.ok(f"frame answering ({how}) after {elapsed:.0f}s")
                result["aliveAfterSeconds"] = round(elapsed, 1)
                return result
            time.sleep(2.0)
        ui.warn(f"frame still silent after {wait_s:.0f}s - powered but not booted, or not booting")
        result["aliveAfterSeconds"] = None
    return result


def cycle(*, reason: str = "", accept_wear: bool = False, off_s: float | None = None,
          wait_s: float = 180.0) -> dict[str, Any]:
    """One off, one on. The fewest relay operations that constitute a power cut."""
    dark_for = RELAY_CYCLE_OFF_S if off_s is None else off_s
    down = off(reason=reason or "power cycle", accept_wear=accept_wear)

    ui.step(f"Holding power off for {dark_for:.0f}s")
    time.sleep(dark_for)

    # The minimum interval is measured from the last operation, and the hold above is
    # normally shorter than it. Waiting the remainder is not optional politeness - it is
    # the rapid-cycle rule, and a cycle that bypassed it would be the exact behaviour the
    # guard exists to stop.
    ledger = _load_ledger()
    if ledger.get("operations"):
        gap = time.time() - float(ledger["operations"][-1].get("epoch", 0))
        if gap < RELAY_MIN_INTERVAL_S:
            remaining = RELAY_MIN_INTERVAL_S - gap
            ui.info(f"rapid-cycle guard: waiting a further {remaining:.0f}s before restoring power")
            time.sleep(remaining)

    up = on(reason=reason or "power cycle", accept_wear=accept_wear, wait_s=wait_s)
    return {
        "action": "cycle",
        "relayOperations": down.get("relayOperations", 0) + up.get("relayOperations", 0),
        "offSeconds": dark_for,
        "aliveAfterSeconds": up.get("aliveAfterSeconds"),
    }


def status() -> dict[str, Any]:
    """Report relay and frame state without touching the relay."""
    state = relay_state()
    alive, how = frame_alive()
    ledger = _load_ledger()
    recent = [op for op in ledger.get("operations", []) if time.time() - float(op.get("epoch", 0)) < 3600]
    info = {
        "entity": HA_ENTITY,
        "relay": state,
        "frame": f"alive ({how})" if alive else "silent",
        "lifetimeRelayOperations": ledger.get("lifetimeOperations", 0),
        "operationsLastHour": len(recent),
    }
    ui.kv(info)
    if state == "on" and not alive:
        ui.warn("relay is on but the frame is silent - it is powered and not answering (booting, or hung)")
    if state == "off" and alive:
        ui.warn(
            "relay reports off yet the frame answers - FL_HA_ENTITY may name the wrong switch. "
            "Resolve this before running `power off`."
        )
    return info


def prove_capability(outcome: dict[str, Any]) -> None:
    """Record what a power command actually demonstrated.

    "Proven" is reserved for a run in which the relay really moved. A command that found
    the plug already in the requested state exercised the token, the entity lookup and the
    state read - worth recording, but it is not evidence that the harness can switch
    hardware, and recording it as such would put a false green in the one place a resuming
    session trusts without re-checking.
    """
    operations = int(outcome.get("relayOperations", 0))
    detail = (
        f"{outcome.get('action')}: {operations} relay operation(s), wrong-entity guard "
        f"{'TRIPPED' if outcome.get('wrongEntity') else 'passed'}"
    )
    if operations > 0:
        progress.prove("power-cycle", by="fl.py power", detail=detail)
    else:
        progress.mark(
            "power-cycle",
            "unproven",
            detail=(
                f"{detail} - Home Assistant reachable and the entity resolved, but the relay "
                "was already in the requested state so nothing was switched."
            ),
        )
