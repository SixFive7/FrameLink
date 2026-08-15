"""Push a built agent to the mule and make it the running service.

This is the half of the closed loop that turns "a binary exists on the workstation" into
"the change is live on the frame". The verification at the end is what makes it count:
the mule's own ``sha256sum`` must equal the hash the build recorded, so *reaching* the
mule is proven by the target rather than assumed by the sender.

Idempotency (CLAUDE.md section 0.1)
-----------------------------------
A second run with the same binary and the same unit does nothing at all - no upload, no
``daemon-reload``, no restart - and says so. Each of the three mutations is guarded by a
comparison against the state already on the device:

* binary   - remote ``sha256sum`` vs the locally recorded hash;
* unit     - remote file content vs the template, byte for byte;
* restart  - performed only when one of the above actually changed, or ``--force``.

The upload itself is staged and renamed rather than written in place. A running ELF cannot
be opened for writing (``ETXTBSY``), but it *can* be replaced by ``rename(2)``, which is
also atomic - so there is no instant at which ``/usr/local/bin/fl-agent`` is a half-written
file, even if the link drops mid-deploy.

Elevation
---------
Everything root-shaped here goes through :meth:`ssh.Mule.run_privileged`, which answers
sudo's password prompt on stdin where the image needs one. Two consequences shape the code:
each privileged step is its own call rather than an ``&&`` chain, because only the first
sudo in a shell line can be answered that way; and the unit file is staged to ``/tmp`` as
the login user and then installed as root, because a ``sudo tee`` heredoc would occupy the
very stdin the password needs. Neither is a workaround - both read better than what they
replaced, since a failure now names the step it happened in.
"""

from __future__ import annotations

from typing import Any

from . import build as build_mod
from . import progress, ssh, ui
from .config import (
    BINARY_NAME,
    REMOTE_BIN,
    REMOTE_STAGE,
    REMOTE_UNIT,
    REMOTE_UNIT_STAGE,
    UNIT_NAME,
    UNIT_TEMPLATE,
    HarnessError,
    require_password,
)


def unit_text() -> str:
    """The unit file as it will exist on the mule, with newlines normalised to LF.

    The template lives in a Windows checkout, so it may carry CRLF depending on git's
    autocrlf. Normalising here means the byte-for-byte comparison against the remote copy
    compares content, not line endings - otherwise every deploy would "detect a change"
    and restart the service forever.
    """
    return UNIT_TEMPLATE.read_text(encoding="utf-8").replace("\r\n", "\n")


def deploy(*, force: bool = False, restart: bool = True, journal_lines: int = 20) -> dict[str, Any]:
    """Install the built binary and unit on the mule and report the resulting state."""
    # Checked before anything else so the first thing a credential-less session hears is
    # the credential problem, not a downstream consequence of it.
    require_password()

    release = build_mod.current_release()
    if release is None:
        raise HarnessError(
            "Nothing to deploy - build/out holds no binary.",
            exit_code=3,
            remedy="Run `python tools/harness/fl.py build` first.",
        )

    local_sha, local_size = build_mod.hash_file(build_mod.BINARY_PATH)
    if local_sha != release["sha256"]:
        raise HarnessError(
            "build/out/agent-release.json does not match the binary beside it - rebuild.",
            exit_code=6,
        )

    wanted_unit = unit_text()
    outcome: dict[str, Any] = {
        "host": None,
        "version": release["version"],
        "sha256": local_sha,
        "sizeBytes": local_size,
        "binaryChanged": False,
        "unitChanged": False,
        "restarted": False,
        "sudoMode": None,
    }

    with ssh.connect() as mule:
        outcome["host"] = mule.host
        ui.step(f"Deploying {release['version']} to {mule.user}@{mule.host}")

        # Probed up front rather than at the first root-shaped step, so an image the
        # harness cannot elevate on is diagnosed before anything has been uploaded.
        outcome["sudoMode"] = "sudo -S (password on stdin)" if mule.sudo_needs_password() else "NOPASSWD"

        # --- binary ---------------------------------------------------------
        probe = mule.run(f"sha256sum {REMOTE_BIN} 2>/dev/null | cut -d' ' -f1")
        remote_sha = probe.stdout.strip()
        if remote_sha == local_sha and not force:
            ui.ok(f"binary already current ({local_sha[:12]}...) - nothing to upload")
        else:
            ui.info(f"uploading {local_size:,} bytes to {REMOTE_STAGE}")
            mule.put(build_mod.BINARY_PATH, REMOTE_STAGE, mode=0o755)

            # Verify the upload before it is allowed anywhere near /usr/local/bin. A
            # truncated transfer that got installed would be a bricked service that looks
            # like a code bug.
            staged = mule.run(f"sha256sum {REMOTE_STAGE} | cut -d' ' -f1").check("hashing the staged upload")
            if staged.stdout.strip() != local_sha:
                mule.run(f"rm -f {REMOTE_STAGE}")
                raise HarnessError(
                    f"Upload corrupted: mule hashed {staged.stdout.strip()}, expected {local_sha}.",
                    exit_code=5,
                )

            # Three steps where there was one `&&` chain. The atomic-rename property is
            # unchanged - install to .new, then rename over - but each root step is now its
            # own sudo, and .check() names which one failed instead of reporting the whole
            # chain's exit status.
            mule.run_privileged(
                f"install -m 0755 -o root -g root {REMOTE_STAGE} {REMOTE_BIN}.new"
            ).check("installing the binary")
            mule.run_privileged(f"mv -f {REMOTE_BIN}.new {REMOTE_BIN}").check(
                "renaming the binary into place"
            )
            mule.run(f"rm -f {REMOTE_STAGE}")
            outcome["binaryChanged"] = True
            ui.ok(f"installed {REMOTE_BIN}")

        # --- unit -----------------------------------------------------------
        existing_unit = mule.read_text(REMOTE_UNIT) or ""
        if existing_unit == wanted_unit and not force:
            ui.ok(f"{UNIT_NAME} already current")
        else:
            # Heredoc with a quoted delimiter: the shell performs no expansion, so the unit
            # arrives byte-identical to the template no matter what it contains. It is
            # written unprivileged to /tmp because the heredoc *is* stdin, and on an image
            # without a NOPASSWD rule stdin is where sudo reads the password from.
            mule.run(
                f"cat > {REMOTE_UNIT_STAGE} << 'FL_UNIT_EOF'\n{wanted_unit}FL_UNIT_EOF"
            ).check("staging the unit file")
            mule.run_privileged(
                f"install -m 0644 -o root -g root {REMOTE_UNIT_STAGE} {REMOTE_UNIT}"
            ).check("writing the unit file")
            mule.run(f"rm -f {REMOTE_UNIT_STAGE}")
            mule.run_privileged("systemctl daemon-reload").check("systemctl daemon-reload")
            outcome["unitChanged"] = True
            ui.ok(f"wrote {REMOTE_UNIT} and reloaded systemd")

        # --- enablement -----------------------------------------------------
        enabled = mule.run(f"systemctl is-enabled {UNIT_NAME} 2>/dev/null").stdout.strip()
        if enabled != "enabled":
            mule.run_privileged(f"systemctl enable {UNIT_NAME}").check("enabling the unit")
            ui.ok(f"{UNIT_NAME} enabled")
        else:
            ui.ok(f"{UNIT_NAME} already enabled")

        # --- restart --------------------------------------------------------
        changed = outcome["binaryChanged"] or outcome["unitChanged"]
        if restart and (changed or force):
            mule.run_privileged(f"systemctl restart {UNIT_NAME}", timeout=90).check(
                "restarting the service"
            )
            outcome["restarted"] = True
            ui.ok("service restarted")
        elif restart:
            ui.info("nothing changed - service left running (use --force to restart anyway)")

        # --- report ---------------------------------------------------------
        state = mule.run(
            f"systemctl is-active {UNIT_NAME}; systemctl is-enabled {UNIT_NAME}; "
            f"systemctl show {UNIT_NAME} -p MainPID -p NRestarts -p ActiveEnterTimestamp --value"
        )
        lines = [ln for ln in state.stdout.splitlines() if ln.strip()]
        outcome["isActive"] = lines[0] if lines else "unknown"
        outcome["isEnabled"] = lines[1] if len(lines) > 1 else "unknown"

        verify = mule.run(f"sha256sum {REMOTE_BIN} | cut -d' ' -f1").check("verifying the installed binary")
        outcome["verifiedSha256"] = verify.stdout.strip()
        if outcome["verifiedSha256"] != local_sha:
            raise HarnessError(
                "The binary on the mule does not hash to what was built - deploy did not take.",
                exit_code=5,
            )

        ui.kv(
            {
                "installed": REMOTE_BIN,
                "sha256": outcome["verifiedSha256"],
                "is-active": outcome["isActive"],
                "is-enabled": outcome["isEnabled"],
            }
        )

        if journal_lines:
            tail = mule.run_privileged(
                f"journalctl -u {UNIT_NAME} -n {int(journal_lines)} --no-pager -o short-iso"
            )
            if tail.stdout.strip():
                ui.block(f"journal: {UNIT_NAME} (last {journal_lines})", tail.stdout)
            outcome["journalTail"] = tail.stdout

    progress.set_artifact(
        "deployed",
        {
            "host": outcome["host"],
            "binary": REMOTE_BIN,
            "version": outcome["version"],
            "sha256": outcome["verifiedSha256"],
            "isActive": outcome["isActive"],
            "isEnabled": outcome["isEnabled"],
            "sudoMode": outcome["sudoMode"],
            "deployedUtc": progress.utcnow(),
        },
    )
    progress.bump("deploys")
    progress.prove(
        "deploy",
        by="fl.py deploy",
        detail=(
            f"{outcome['version']} verified on {outcome['host']} by remote sha256sum; "
            f"unit {outcome['isActive']}/{outcome['isEnabled']}; elevation {outcome['sudoMode']}"
        ),
    )

    # The M0 acceptance condition: a change reached the mule and the mule itself proved it.
    if outcome["binaryChanged"]:
        progress.prove(
            "closed-loop",
            by="fl.py build + fl.py deploy",
            detail=(
                f"{BINARY_NAME} {outcome['version']} built on the workstation and verified "
                f"present on {outcome['host']} by its own sha256sum, with no human step."
            ),
        )
    return outcome
