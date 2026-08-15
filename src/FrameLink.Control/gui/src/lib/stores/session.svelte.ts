/**
 * Which of the three top-level conditions the whole app is in.
 *
 * The Fleet Manager has exactly three: it has no operator password (§3.2), it has one and
 * nobody has signed in, or it has one and somebody has. Every route in the app belongs to
 * exactly one of those, and the root layout routes on this rather than each screen guarding
 * itself.
 *
 * Discovering which one requires two probes, in this order:
 *
 *  1. `GET /api/status` — reachable with no session by design (`OperatorGate` exempts it),
 *     and the only way to learn that the instance is unconfigured.
 *  2. `GET /api/devices` — 200 proves a live session, 401 proves there is not one. There is
 *     no "am I signed in" endpoint, so the cheapest gated route stands in for it.
 */

import { api, ApiError, handleUnauthorized } from '$lib/api/client';
import type { SetupStatus } from '$lib/api/types';

export type SessionPhase = 'starting' | 'unconfigured' | 'signed-out' | 'signed-in' | 'unreachable';

class SessionState {
	phase = $state<SessionPhase>('starting');

	/** The setup status from the last probe. Drives the setup screen's content. */
	setup = $state<SetupStatus | undefined>();

	/** Why the server could not be reached, when `phase` is `unreachable`. */
	problem = $state<string | undefined>();

	constructor() {
		// One place handles an expired session for the whole app. Without this, every poll on
		// every screen would have to decide what a 401 means.
		handleUnauthorized(() => {
			if (this.phase === 'signed-in') this.phase = 'signed-out';
		});
	}

	/** Runs both probes. Safe to call again — the login and setup screens both re-run it. */
	async bootstrap(): Promise<void> {
		try {
			this.setup = await api.status();
		} catch (cause) {
			this.phase = 'unreachable';
			this.problem =
				cause instanceof Error ? cause.message : 'The Fleet Manager did not answer /api/status.';
			return;
		}

		if (!this.setup.configured) {
			this.phase = 'unconfigured';
			return;
		}

		try {
			await api.devices(false, { quiet: true });
			this.phase = 'signed-in';
		} catch (cause) {
			if (cause instanceof ApiError && cause.isUnauthorized) {
				this.phase = 'signed-out';
				return;
			}
			this.phase = 'unreachable';
			this.problem = cause instanceof Error ? cause.message : 'The Fleet Manager did not answer.';
		}
	}

	async signIn(password: string): Promise<void> {
		await api.signIn(password);
		this.phase = 'signed-in';
	}

	async signOut(): Promise<void> {
		try {
			await api.signOut();
		} finally {
			this.phase = 'signed-out';
		}
	}
}

export const session = new SessionState();
