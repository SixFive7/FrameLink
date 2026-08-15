/**
 * The presence ladder of version2.md §3.5, derived from what `DeviceView` actually carries.
 *
 * §3.5 names five states: `Online`, `Online-Degraded`, `Incompatible`, `Offline` (with
 * *offline since*) and `Never enrolled`. Presence *is* the socket, and the socket is `online`;
 * the rest is read off the same row.
 *
 * The derivation, in the order it is evaluated:
 *
 *  1. **incompatible** — `protocolCompatible` is false, or no handshake has ever completed for
 *     this row. Ranked first because the *reason* the frame is not here is the useful fact:
 *     `DeviceHandshake` answers `version-mismatch` and closes, so such a device is offline by
 *     construction and saying only "offline" would hide the cause.
 *  2. **online-degraded** — a socket is open and the server classified the agent's self-report
 *     as `degraded` or `halted`.
 *  3. **online** — a socket is open and nothing says otherwise.
 *  4. **never-enrolled** — adopted, but not seen since it was adopted. See below.
 *  5. **offline** — no socket. `lastSeenUtc` is the "offline since" §3.5 asks for.
 *
 * Two things this file no longer does, both of which it used to do wrongly:
 *
 *  - **It does not parse `agentStatus`.** That field is free text by protocol definition, and
 *    matching it against the §2.3 vocabulary in a browser made the GUI a second consumer of a
 *    string the agent may reword at will. It is now classified once, by the server, into
 *    `health`. A real agent's self-report reads `Progressing(linux-arm64, endpoints resolved by
 *    boot file)`; under the old code the whole sentence failed to equal `InSync` and every
 *    healthy frame in the fleet rendered as *Online — degraded*.
 *  - **It does not treat `never-enrolled` as "no protocol version".** A row only exists after a
 *    proven handshake, so that branch was unreachable. The reading an operator actually needs is
 *    the UniFi one — *adopted, and has not checked in since* — which is `lastSeenUtc` earlier
 *    than `stateChangedUtc`, and needed a field `DeviceView` did not project until now.
 */

import type { DeviceView } from './api/types';

export type Presence =
	| 'online'
	| 'online-degraded'
	| 'incompatible'
	| 'offline'
	| 'never-enrolled';

export function presenceOf(device: DeviceView): Presence {
	if (!device.protocolCompatible || device.protocolVersion === undefined) return 'incompatible';

	if (device.online) {
		return device.health === 'degraded' || device.health === 'halted'
			? 'online-degraded'
			: 'online';
	}

	// Adopted a while ago and never seen since. The frame was told to be here and is not, which
	// is a different fact from "was here, is not now" and reads very differently on a bench.
	if (device.state === 'adopted' && Date.parse(device.lastSeenUtc) < Date.parse(device.stateChangedUtc)) {
		return 'never-enrolled';
	}

	return 'offline';
}

/** True when the degradation is "busy", not "broken" — used to pick the wording, not the state. */
export function isWorking(device: DeviceView): boolean {
	return device.health === 'working';
}

interface PresenceDescriptor {
	/** Sentence-case label. Rendered exactly as §3.5 names the state. */
	readonly label: string;
	/** Which semantic colour family the badge and dot draw from. */
	readonly tone: 'ok' | 'warn' | 'danger' | 'muted' | 'info';
	/** One line an operator can act on. */
	readonly meaning: string;
	/** Sort weight: lower sorts first. Trouble rises to the top of the fleet list. */
	readonly weight: number;
}

export const PRESENCE: Record<Presence, PresenceDescriptor> = {
	incompatible: {
		label: 'Incompatible',
		tone: 'danger',
		meaning:
			'The agent speaks a different protocol version than this server. It will update itself ' +
			'on its next hourly check and come back.',
		weight: 0
	},
	'online-degraded': {
		label: 'Online — degraded',
		tone: 'warn',
		meaning: 'Connected, but the agent is reporting something other than in-sync.',
		weight: 1
	},
	'never-enrolled': {
		label: 'Never enrolled',
		tone: 'warn',
		meaning:
			'Adopted, but this frame has not connected once since. Check that it is powered on and ' +
			'pointed at this Fleet Manager.',
		weight: 2
	},
	offline: {
		label: 'Offline',
		tone: 'muted',
		meaning: 'No socket. The frame keeps running the product if it was healthy when contact dropped.',
		weight: 3
	},
	online: {
		label: 'Online',
		tone: 'ok',
		meaning: 'Connected and in sync.',
		weight: 4
	}
};

/** Descriptor lookup, so a component never indexes the record itself. */
export function describePresence(device: DeviceView): PresenceDescriptor & { presence: Presence } {
	const presence = presenceOf(device);

	// "Busy" is still online, and saying so is the difference between an operator watching a
	// reboot happen and an operator opening a ticket about it.
	if (presence === 'online' && isWorking(device)) {
		return {
			...PRESENCE.online,
			presence,
			tone: 'info',
			label: 'Online — working',
			meaning: 'Connected, and the agent is part-way through applying something.'
		};
	}

	return { ...PRESENCE[presence], presence };
}
