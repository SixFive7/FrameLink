"""Paramiko wrapper for every command the harness runs on the mule.

CLAUDE.md section 1.3 names paramiko as the sanctioned tool for remote execution, and section 1.2 makes
the password handling non-negotiable:

* the password comes from ``FL_PW`` and nowhere else;
* it is passed to paramiko as an argument and held in no other variable, file or log;
* ``allow_agent=False, look_for_keys=False`` - without these paramiko reaches for the
  Windows SSH agent and fails with "key cannot be used for signing", and more importantly
  a key fallback would mean authenticating as somebody the session was not given
  permission to be. There is deliberately no fallback of any kind.

Every entry point checks for ``FL_PW`` before opening a socket, so the failure is a named
error in the first second rather than a hang on a password prompt.

Elevation
---------
The harness logs in as ``framelink``, an ordinary user, and several of the things it must do
need root: installing a binary into ``/usr/local/bin``, writing a unit into
``/etc/systemd/system``, reading ``/dev/fb0``, tailing the system journal.
:meth:`Mule.run_privileged` is the single path for all of it, and it adapts to the unit in
front of it rather than assuming a sudo policy:

* ``sudo -n -k true`` is probed **once per connection** and the answer cached. ``-n`` refuses
  to prompt and ``-k`` ignores any cached sudo timestamp, so the answer is a fact about
  ``/etc/sudoers`` rather than a side effect of some sudo that ran five minutes ago.
* If that succeeds a NOPASSWD rule exists, and plain ``sudo`` is used.
* If it does not, ``sudo -S -k -p ''`` is used and ``FL_PW`` is written to the command's
  **stdin**. ``-S`` makes sudo read the password from there, ``-p ''`` suppresses the prompt
  so it can never be mistaken for command output, and ``-k`` means the credential is
  genuinely exercised on every call rather than a stale timestamp being trusted.

The password therefore never appears in the command text, which is what keeps it out of the
mule's ``ps`` table, its shell history and its auth log. Section 1.2 allows no other channel.

A stock Raspberry Pi OS Lite image is the second case, measured on the mule 2026-08-15: the
first user is in the ``sudo`` group, ``/etc/sudoers.d`` holds no NOPASSWD drop-in, and
``sudo -n true`` answers ``sudo: a password is required``. This is a harness concern only -
``assets/fl-agent.service`` sets ``User=root``, so systemd starts the agent as root and it
never invokes sudo at all.
"""

from __future__ import annotations

import socket
from contextlib import contextmanager
from dataclasses import dataclass

from . import ui
from .config import (
    MULE_HOST,
    MULE_SSH_PORT,
    MULE_USER,
    ElevationError,
    HarnessError,
    require_password,
)

#: Asks sudo what the *policy* is, with no prompting (``-n``) and no credit for a cached
#: timestamp (``-k``). Exit 0 means a NOPASSWD rule covers this user.
SUDO_PROBE = "sudo -n -k true"

#: Prefix for a privileged command on a unit that wants a password. The password follows on
#: stdin; nothing about it is in this string.
SUDO_WITH_PASSWORD = "sudo -S -k -p ''"

#: Prefix for a privileged command on a unit with a NOPASSWD rule.
SUDO_PASSWORDLESS = "sudo"

#: sudo prints this on stderr whenever ``/etc/hosts`` does not resolve the machine's own
#: hostname - routine on a freshly flashed Pi that has just been renamed, and harmless: the
#: command still runs and still exits 0. It must never be read as an elevation failure.
SUDO_BENIGN_STDERR = ("unable to resolve host",)


@dataclass(frozen=True)
class Result:
    """Outcome of one remote command."""

    command: str
    exit_status: int
    stdout: str
    stderr: str

    @property
    def ok(self) -> bool:
        return self.exit_status == 0

    def check(self, what: str) -> "Result":
        """Raise a diagnosed error unless the command succeeded."""
        if not self.ok:
            detail = (self.stderr or self.stdout).strip() or "(no output)"
            raise HarnessError(
                f"{what} failed on the mule (exit {self.exit_status}): {detail}",
                exit_code=5,
            )
        return self


class Mule:
    """An open SSH session to the development mule."""

    def __init__(self, client, host: str, user: str):
        self._client = client
        self.host = host
        self.user = user
        # None until the first privileged command asks. Cached for the lifetime of the
        # connection: the answer is a property of /etc/sudoers, which nothing the harness
        # does changes, and probing once per command would triple the round trips.
        self._sudo_needs_password: bool | None = None

    def run(self, command: str, *, timeout: float = 120.0) -> Result:
        """Run a command and capture its stdout and stderr as text.

        ``get_pty=False`` deliberately: a PTY merges stderr into stdout and injects
        carriage returns, which corrupts anything the harness wants to compare byte for
        byte (CLAUDE.md section 1.4).
        """
        _, stdout, stderr = self._client.exec_command(command, get_pty=False, timeout=timeout)
        out = stdout.read().decode("utf-8", errors="replace")
        err = stderr.read().decode("utf-8", errors="replace")
        status = stdout.channel.recv_exit_status()
        return Result(command=command, exit_status=status, stdout=out, stderr=err)

    def run_binary(self, command: str, *, timeout: float = 120.0) -> tuple[int, bytes, str]:
        """Run a command whose stdout is binary - a PNG from ``grim``, framebuffer bytes.

        Returns ``(exit_status, stdout_bytes, stderr_text)``. Kept separate from
        :meth:`run` so no binary payload is ever put through a text decode.
        """
        _, stdout, stderr = self._client.exec_command(command, get_pty=False, timeout=timeout)
        data = stdout.read()
        err = stderr.read().decode("utf-8", errors="replace")
        status = stdout.channel.recv_exit_status()
        return status, data, err

    # --- elevation ---------------------------------------------------------
    def sudo_needs_password(self) -> bool:
        """Whether elevating on this unit needs the login password. Probed once, cached.

        Three outcomes, and the third is the one worth naming: a user who cannot elevate at
        all is a different problem from one who must type a password, and no amount of
        password answering will fix it. Saying so here means the diagnosis appears once, at
        the first privileged command, rather than as a puzzling failure of whichever
        ``install`` or ``systemctl`` happened to run first.
        """
        if self._sudo_needs_password is None:
            probe = self.run(SUDO_PROBE, timeout=30.0)
            if probe.ok:
                self._sudo_needs_password = False
                ui.info("sudo: a NOPASSWD rule covers this login - elevating without a password")
            elif "password" in (probe.stderr + probe.stdout).lower():
                self._sudo_needs_password = True
                ui.info("sudo: this login must authenticate - answering on stdin from FL_PW")
            else:
                complaint = (probe.stderr or probe.stdout).strip() or "(no output)"
                raise ElevationError(
                    f"{self.user}@{self.host} cannot elevate at all: {complaint}",
                    exit_code=3,
                    remedy=(
                        "The harness needs root on the mule to install a binary, write a unit "
                        "and read /dev/fb0. A stock Raspberry Pi OS image puts the first user "
                        "in the sudo group; check FL_USER names that user, or add this one to "
                        "the sudo group on the frame."
                    ),
                )
        return self._sudo_needs_password

    def _exec_privileged(self, command: str, *, timeout: float):
        """Start ``command`` under sudo, answering the password read if there is one.

        Returns the wrapped command text and paramiko's stdout/stderr handles, so both the
        text and the binary variants share one elevation decision and one stdin dance.
        """
        if command.lstrip().startswith("sudo"):
            # A bare sudo inside a privileged command means two sudo invocations in one
            # line, and only the first would find the password on stdin. Caught here rather
            # than left to fail confusingly on the mule.
            raise ValueError(
                f"run_privileged adds sudo itself; pass the bare command, not {command!r}"
            )

        password: str | None = None
        if self.sudo_needs_password():
            # Read FL_PW *before* the channel is opened. An unset variable must produce the
            # named credential error, not a remote sudo sitting on a stdin that will never
            # carry an answer.
            password = require_password()
            wrapped = f"{SUDO_WITH_PASSWORD} {command}"
        else:
            wrapped = f"{SUDO_PASSWORDLESS} {command}"

        stdin, stdout, stderr = self._client.exec_command(wrapped, get_pty=False, timeout=timeout)
        if password is not None:
            try:
                stdin.write(password + "\n")
                stdin.flush()
            except OSError:
                # The remote end can close stdin before this lands - a command that never
                # reads it, or a sudo that already gave up. The exit status is the real
                # signal, so this is not itself the failure.
                pass
            finally:
                # Closing the write side matters: sudo allows three attempts, and with the
                # stream left open a rejected password would sit waiting for a second one
                # that is never coming. EOF turns that hang into an immediate, named exit.
                try:
                    stdin.channel.shutdown_write()
                except OSError:
                    pass
        return wrapped, stdout, stderr

    def run_privileged(self, command: str, *, timeout: float = 120.0) -> Result:
        """Run one command as root and capture its stdout and stderr as text.

        Pass the bare command - ``install -m 0755 a b``, not ``sudo install ...``. Exactly
        one sudo invocation per call, because only the first one in a shell line can be
        answered on stdin.
        """
        wrapped, stdout, stderr = self._exec_privileged(command, timeout=timeout)
        out = stdout.read().decode("utf-8", errors="replace")
        err = stderr.read().decode("utf-8", errors="replace")
        status = stdout.channel.recv_exit_status()
        result = Result(command=wrapped, exit_status=status, stdout=out, stderr=err)
        self._raise_if_elevation_failed(result)
        return result

    def run_privileged_binary(self, command: str, *, timeout: float = 120.0) -> tuple[int, bytes, str]:
        """:meth:`run_privileged` for a command whose stdout is binary - a framebuffer read."""
        wrapped, stdout, stderr = self._exec_privileged(command, timeout=timeout)
        data = stdout.read()
        err = stderr.read().decode("utf-8", errors="replace")
        status = stdout.channel.recv_exit_status()
        self._raise_if_elevation_failed(
            Result(command=wrapped, exit_status=status, stdout="", stderr=err)
        )
        return status, data, err

    def _raise_if_elevation_failed(self, result: Result) -> None:
        """Separate "sudo refused" from "the command ran as root and failed".

        Both arrive as a non-zero exit status, and conflating them is expensive: a wrong
        password would be reported as ``systemctl restart failed`` and send the reader
        looking at the unit. sudo prefixes its own complaints with ``sudo:``, so a non-zero
        exit carrying such a line is an elevation failure and nothing else.
        """
        if result.ok:
            return
        for line in result.stderr.splitlines():
            text = line.strip()
            if not text.startswith("sudo:"):
                continue
            if any(benign in text for benign in SUDO_BENIGN_STDERR):
                continue
            raise ElevationError(
                f"sudo refused on {self.user}@{self.host}: {text}",
                exit_code=3,
                remedy=(
                    "FL_PW must be the login password of this user on this unit - the harness "
                    "answers sudo with it on stdin. Check it is right for this frame, and that "
                    "FL_USER names a user in the sudo group. The password is never stored, so "
                    "a stale one can only come from the environment of this session."
                ),
            )

    def put(self, local_path, remote_path: str, *, mode: int = 0o644) -> int:
        """Upload a file over SFTP. Returns the byte count written."""
        sftp = self._client.open_sftp()
        try:
            sftp.put(str(local_path), remote_path)
            sftp.chmod(remote_path, mode)
            return sftp.stat(remote_path).st_size or 0
        finally:
            sftp.close()

    def read_text(self, remote_path: str) -> str | None:
        """Read a remote text file, or None if it does not exist."""
        result = self.run(f"cat {remote_path} 2>/dev/null")
        return result.stdout if result.ok else None


@contextmanager
def connect(*, host: str = MULE_HOST, user: str = MULE_USER, timeout: float = 15.0):
    """Open a session to the mule, or fail with a diagnosis.

    The password is read from the environment at the moment of use and passed straight
    through; this function keeps no copy, and neither the host key nor the credential is
    persisted anywhere.
    """
    password = require_password()

    try:
        import paramiko
    except ImportError as exc:  # pragma: no cover - environment problem, not logic
        raise HarnessError(
            f"paramiko is not importable: {exc}",
            exit_code=4,
            remedy="CLAUDE.md section 1.3 lists paramiko 4.x as already installed on this workstation.",
        ) from exc

    client = paramiko.SSHClient()
    # AutoAddPolicy: the mule is reflashed constantly, so its host key changes as a matter
    # of routine. Pinning it would mean a manual known_hosts edit after every wipe, and the
    # harness deliberately writes nothing to the user's SSH configuration (section 1.2).
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    try:
        client.connect(
            host,
            port=MULE_SSH_PORT,
            username=user,
            password=password,
            allow_agent=False,
            look_for_keys=False,
            timeout=timeout,
        )
    except paramiko.AuthenticationException as exc:
        raise HarnessError(
            f"Authentication as {user}@{host} was rejected.",
            exit_code=3,
            remedy="FL_PW is set but wrong for this unit, or the username differs (override with FL_USER).",
        ) from exc
    except (OSError, socket.error, paramiko.SSHException) as exc:
        raise HarnessError(
            f"Cannot reach {user}@{host}:{MULE_SSH_PORT} - {type(exc).__name__}: {exc}",
            exit_code=3,
            remedy=(
                "Check the mule is powered (fl.py power state) and on the network. "
                "Override the address with FL_HOST."
            ),
        ) from exc

    try:
        yield Mule(client, host, user)
    finally:
        client.close()


def is_alive(host: str = MULE_HOST, *, port: int = MULE_SSH_PORT, timeout: float = 2.0) -> bool:
    """True if the frame answers a TCP connection on its SSH port.

    Used by the relay guard, alongside ICMP. A TCP handshake to port 22 is the stronger of
    the two signals - it proves the OS is up and serving, not merely that some device holds
    the address - and it needs no elevation or ICMP permission on Windows.
    """
    sock = socket.socket()
    sock.settimeout(timeout)
    try:
        sock.connect((host, port))
        return True
    except OSError:
        return False
    finally:
        sock.close()
