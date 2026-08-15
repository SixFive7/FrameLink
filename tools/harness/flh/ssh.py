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
"""

from __future__ import annotations

import socket
from contextlib import contextmanager
from dataclasses import dataclass

from .config import (
    MULE_HOST,
    MULE_SSH_PORT,
    MULE_USER,
    HarnessError,
    require_password,
)


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
