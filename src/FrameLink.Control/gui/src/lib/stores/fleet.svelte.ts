/**
 * The device list, kept fresh.
 *
 * Presence is the socket (§3.5), which means the truth about a device changes the instant a
 * frame connects or drops. Two mechanisms keep this list honest about that, and the split
 * between them is deliberate:
 *
 *  - **`GET /api/events`**, a server-sent-event stream carrying nothing but the id of whatever
 *    changed. It is what makes a frame plugged in on the bench appear at once, which is the
 *    moment §3.3 is designed around. Ids only: the row is still read from `/api/devices`, so
 *    there is exactly one place a device is rendered from and no second copy to drift.
 *  - **The poll**, still here, now every 20 seconds. The stream is an optimisation and the
 *    poll is the mechanism — same relationship the handshake has to the hourly update check in
 *    §2.8. A proxy that eats the stream, a browser that refuses it, a dropped event: all cost
 *    a few seconds of staleness rather than a console that quietly stops updating.
 *
 * Discipline that applies to both:
 *
 *  - nothing runs while the tab is hidden, because a console left open on a second monitor
 *    overnight should not hold a stream open or poll 4,000 times;
 *  - both resume immediately on becoming visible, so returning to the tab shows the truth
 *    rather than a stale copy;
 *  - one in-flight request at a time, aborted on teardown.
 *
 * The list is *replaced*, not merged. The server orders by last-seen descending, and that
 * ordering is itself information — the row that just checked in rises to the top. The
 * `reorder` FLIP animation is what stops that from being disorienting.
 */

import { api, ApiError } from '$lib/api/client';
import type { DeviceView } from '$lib/api/types';
import { PRESENCE, presenceOf } from '$lib/presence';

/** The safety net under the event stream, not the primary mechanism. */
const POLL_INTERVAL = 20_000;

/** How long a burst of events is allowed to settle before one refresh is issued. */
const COALESCE_DELAY = 120;

class FleetState {
	devices = $state<DeviceView[]>([]);

	/** §3.3's "show blocked" toggle. Off by default so an accidental block stays reversible. */
	includeBlocked = $state(false);

	/** True until the first response, so the fleet screen can show a skeleton rather than "0 devices". */
	loading = $state(true);

	/** Set when the last poll failed. Cleared by the next success. Never clears the list — a
	    stale fleet with a warning beats an empty one. */
	error = $state<string | undefined>();

	/** When the list was last successfully refreshed. */
	refreshedAt = $state<number | undefined>();

	/** True while the event stream is open. Shown so the console can say which mode it is in. */
	live = $state(false);

	#timer: ReturnType<typeof setTimeout> | undefined;
	#coalesce: ReturnType<typeof setTimeout> | undefined;
	#inflight: AbortController | undefined;
	#stream: EventSource | undefined;
	#running = false;

	get pending(): DeviceView[] {
		return this.devices.filter((device) => device.state === 'pending');
	}

	get adopted(): DeviceView[] {
		return this.devices
			.filter((device) => device.state === 'adopted')
			.sort(
				(a, b) =>
					PRESENCE[presenceOf(a)].weight - PRESENCE[presenceOf(b)].weight ||
					(a.name ?? a.deviceId).localeCompare(b.name ?? b.deviceId)
			);
	}

	get blocked(): DeviceView[] {
		return this.devices.filter((device) => device.state === 'blocked');
	}

	find(deviceId: string): DeviceView | undefined {
		return this.devices.find((device) => device.deviceId === deviceId);
	}

	/** Starts the stream and the poll. Idempotent — calling it twice does not double either. */
	start() {
		if (this.#running) return;
		this.#running = true;

		document.addEventListener('visibilitychange', this.#onVisibility);
		this.#listen();
		void this.refresh();
	}

	stop() {
		this.#running = false;
		document.removeEventListener('visibilitychange', this.#onVisibility);
		clearTimeout(this.#timer);
		clearTimeout(this.#coalesce);
		this.#inflight?.abort();
		this.#inflight = undefined;
		this.#hangUp();
	}

	/**
	 * Opens the event stream, or reopens it.
	 *
	 * `EventSource` reconnects on its own after a network drop, so there is no retry loop here —
	 * only the teardown, which must be exact. A console left open for a week must not accumulate
	 * streams, and the visibility handler closes and reopens rather than leaving one dangling.
	 */
	#listen() {
		if (!this.#running || typeof EventSource === 'undefined') return;
		this.#hangUp();

		const stream = new EventSource('/api/events');
		this.#stream = stream;

		stream.addEventListener('ready', () => (this.live = true));
		stream.addEventListener('device', () => this.#soon());
		stream.addEventListener('error', () => {
			// EventSource is already retrying. Saying so is the point: the poll underneath keeps
			// the list correct, so this is a downgrade in latency and not a failure.
			this.live = false;
		});
	}

	#hangUp() {
		this.#stream?.close();
		this.#stream = undefined;
		this.live = false;
	}

	/** Refreshes once, shortly, however many events arrive in the meantime. */
	#soon() {
		clearTimeout(this.#coalesce);
		this.#coalesce = setTimeout(() => void this.refresh(), COALESCE_DELAY);
	}

	/**
	 * Fetches once and schedules the next poll.
	 *
	 * Blocked rows are always fetched (`includeBlocked=true`) regardless of the toggle, and
	 * the toggle filters for display only. That costs nothing — the server filters with a SQL
	 * `WHERE`, not a join — and it means flipping "show blocked" is instant rather than a
	 * round trip, which matters because the toggle exists for the moment somebody realises
	 * they blocked the wrong frame.
	 */
	async refresh(): Promise<void> {
		clearTimeout(this.#timer);
		this.#inflight?.abort();

		const controller = new AbortController();
		this.#inflight = controller;

		try {
			const response = await api.devices(true, { signal: controller.signal });
			this.devices = response.devices;
			this.error = undefined;
			this.refreshedAt = Date.now();
		} catch (cause) {
			if (controller.signal.aborted) return;
			// A 401 is already handled globally by the session store; re-reporting it here
			// would put a toast on top of the login screen.
			if (!(cause instanceof ApiError && cause.isUnauthorized)) {
				this.error = cause instanceof Error ? cause.message : 'The device list could not be read.';
			}
		} finally {
			if (this.#inflight === controller) this.#inflight = undefined;
			this.loading = false;
			this.#schedule();
		}
	}

	/**
	 * Folds a single device back into the list after an action, so the row updates in the
	 * same frame as the button press instead of on the next poll.
	 */
	merge(device: DeviceView) {
		const index = this.devices.findIndex((existing) => existing.deviceId === device.deviceId);
		this.devices =
			index === -1
				? [device, ...this.devices]
				: this.devices.map((existing, at) => (at === index ? device : existing));
	}

	drop(deviceId: string) {
		this.devices = this.devices.filter((device) => device.deviceId !== deviceId);
	}

	#schedule() {
		if (!this.#running || document.visibilityState === 'hidden') return;
		this.#timer = setTimeout(() => void this.refresh(), POLL_INTERVAL);
	}

	#onVisibility = () => {
		if (document.visibilityState === 'visible') {
			this.#listen();
			void this.refresh();
		} else {
			clearTimeout(this.#timer);
			clearTimeout(this.#coalesce);
			this.#hangUp();
		}
	};
}

export const fleet = new FleetState();
