/**
 * One frame's reconciliation, kept live.
 *
 * **Why this is a stream and not a poll.** Reconciliation reports are telemetry, not presence,
 * so there is no socket in the browser carrying them — but there does not need to be one.
 * `TelemetryIngest.HandleAsync` publishes the device id on `FleetEvents` the instant a report is
 * stored, and its comment says exactly what that is for: *"one nudge per report is enough to
 * make the live reconciliation screen live without a second serialisation"*. So this store
 * listens to the same `GET /api/events` stream the fleet list uses and re-reads
 * `/api/devices/{id}/reconcile` when the nudge names *this* device.
 *
 * The stream is fleet-wide and carries only an id, so on a ten-frame fleet it wakes on every
 * other frame's telemetry too. `fleet.svelte.ts` discards `event.data` because it re-reads the
 * whole list anyway; this screen is about one device, so it filters on the id and skips the
 * round trip for everybody else's traffic.
 *
 * **The poll underneath is 20 s and is the mechanism, not the optimisation** — the same
 * relationship `fleet.svelte.ts` has to its own stream, for the same reasons: a proxy that eats
 * the stream, a browser that refuses it, or one of the 64 queued events dropped under
 * `DropOldest` all cost a few seconds of staleness rather than a console that quietly stops
 * updating. A provision changes state about every twenty seconds — a reboot cycle is 40–60 s and
 * the reports inside one arrive a few seconds apart — so a 20 s floor loses nothing even when
 * the stream is gone entirely.
 *
 * **Why the resource list is merged rather than replaced.** `ReconcileReport.Resources` is
 * documented as "every resource in the catalog", and mid-pass it is not. `ReconcileLoop`
 * publishes three kinds of report: the end-of-pass one carries everything, the `act` one carries
 * only the resources walked so far this pass, and the one sent immediately before a reboot
 * carries **exactly one**. Replacing the list on every report would collapse a 78-row screen to
 * a single row for the length of every reboot — which is most of a provision. So a report that
 * covers the whole catalog replaces the cache (its ordering is authoritative — the loop sorts
 * topologically), and a partial one updates the rows it carries and leaves the rest alone.
 */

import { api, ApiError } from '$lib/api/client';
import type { DeviceEvent, ReconcileReport, ResourceReport } from '$lib/api/types';

/** The safety net under the event stream, not the primary mechanism. */
const POLL_INTERVAL = 20_000;

/** How long a burst of events is allowed to settle before one refresh is issued. */
const COALESCE_DELAY = 120;

/** How much history the timeline asks for. The server's own default, stated rather than implied. */
const EVENT_LIMIT = 50;

class ReconcileState {
	/** The frame being watched, or undefined before the first `watch()`. */
	deviceId = $state<string | undefined>();

	/** The most recent report, however partial its resource list. Drives the live strip. */
	latest = $state<ReconcileReport | undefined>();

	/**
	 * Every resource this frame has ever reported, newest status per name, in dependency order.
	 * Merged rather than replaced — see the header.
	 */
	resources = $state<ResourceReport[]>([]);

	/** Recent history, newest first, exactly as the server ordered it. */
	events = $state<DeviceEvent[]>([]);

	/** Whether a socket is open right now. Presence *is* the socket (§3.5). */
	online = $state(false);

	/** True once a fetch has answered and the frame had never sent a report. A state, not an error. */
	neverReported = $state(false);

	/** True until the first response, either way. */
	loading = $state(true);

	/** Set when the last refresh failed. Never clears what is already on screen. */
	problem = $state<string | undefined>();

	/** True while the event stream is open, so the screen can say which mode it is in. */
	live = $state(false);

	/** When the report was last successfully re-read. */
	refreshedAt = $state<number | undefined>();

	#timer: ReturnType<typeof setTimeout> | undefined;
	#coalesce: ReturnType<typeof setTimeout> | undefined;
	#inflight: AbortController | undefined;
	#stream: EventSource | undefined;
	#running = false;

	/** The resources that have never been heard from, when the catalog is bigger than the cache. */
	get unreported(): number {
		if (!this.latest) return 0;
		return Math.max(0, this.latest.inSync + this.latest.rebootsExpected - this.resources.length);
	}

	/**
	 * Starts watching one frame. Switching device resets everything — a cache keyed by name
	 * would otherwise carry one frame's resources onto another's screen.
	 */
	watch(deviceId: string) {
		if (this.#running && this.deviceId === deviceId) return;

		this.stop();
		this.deviceId = deviceId;
		this.latest = undefined;
		this.resources = [];
		this.events = [];
		this.neverReported = false;
		this.loading = true;
		this.problem = undefined;
		this.refreshedAt = undefined;

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
	 * `EventSource` reconnects on its own, so there is no retry loop here — only the teardown,
	 * which must be exact. A console left open on a second monitor must not accumulate streams.
	 */
	#listen() {
		if (!this.#running || typeof EventSource === 'undefined') return;
		this.#hangUp();

		const stream = new EventSource('/api/events');
		this.#stream = stream;

		stream.addEventListener('ready', () => (this.live = true));
		stream.addEventListener('device', (event) => {
			// Fleet-wide stream, one screen's worth of interest. The payload is the bare device
			// id, not JSON — `WriteEventAsync(context, "device", deviceId, …)`.
			if ((event as MessageEvent<string>).data === this.deviceId) this.#soon();
		});
		stream.addEventListener('error', () => {
			// EventSource is already retrying, and the poll underneath keeps the screen correct.
			// This is a downgrade in latency, not a failure, and the screen says so.
			this.live = false;
		});
	}

	#hangUp() {
		this.#stream?.close();
		this.#stream = undefined;
		this.live = false;
	}

	/** Refreshes once, shortly, however many nudges arrive in the meantime. */
	#soon() {
		clearTimeout(this.#coalesce);
		this.#coalesce = setTimeout(() => void this.refresh(), COALESCE_DELAY);
	}

	/** Fetches the report and the history, and schedules the next poll. */
	async refresh(): Promise<void> {
		const deviceId = this.deviceId;
		if (!deviceId) return;

		clearTimeout(this.#timer);
		this.#inflight?.abort();

		const controller = new AbortController();
		this.#inflight = controller;

		try {
			// Together rather than in sequence: they are two reads of the same moment, and the
			// history is what carries the plain-language wording the report itself drops.
			const [report, history] = await Promise.all([
				api.reconcile(deviceId, { signal: controller.signal }),
				api.deviceEvents(deviceId, EVENT_LIMIT, { signal: controller.signal })
			]);

			this.online = report.online;
			this.neverReported = report.report === undefined;
			if (report.report) this.#absorb(report.report);

			// Server-ordered, newest first, with a rowid tie-break the client cannot reproduce.
			// Re-sorting by `occurredUtc` would scramble a burst drained from an offline buffer.
			this.events = history.events;

			this.problem = undefined;
			this.refreshedAt = Date.now();
		} catch (cause) {
			if (controller.signal.aborted) return;
			if (!(cause instanceof ApiError && cause.isUnauthorized)) {
				this.problem =
					cause instanceof Error
						? cause.message
						: 'This frame’s reconciliation report could not be read.';
			}
		} finally {
			if (this.#inflight === controller) this.#inflight = undefined;
			this.loading = false;
			this.#schedule();
		}
	}

	/**
	 * Folds one report into the cache.
	 *
	 * A report is a whole-catalog census when its resource list is at least as long as the
	 * catalog — and the catalog size is recoverable from any report at all, because
	 * `rebootsExpected` is computed on the frame as `catalogSize - inSync` from the same `inSync`
	 * the message carries. A census replaces the cache outright, taking its topological ordering
	 * with it; anything shorter updates the rows it names and leaves the others standing.
	 */
	#absorb(report: ReconcileReport) {
		// Reports can overtake each other on a draining buffer; the sequence is monotonic per
		// device and exists precisely so a late one can be recognised and dropped.
		if (this.latest && report.sequence < this.latest.sequence) return;

		this.latest = report;

		if (report.resources.length >= report.inSync + report.rebootsExpected) {
			this.resources = report.resources;
			return;
		}

		const byName = new Map(this.resources.map((resource) => [resource.name, resource]));
		const appended: ResourceReport[] = [];

		for (const resource of report.resources) {
			if (byName.has(resource.name)) byName.set(resource.name, resource);
			else appended.push(resource);
		}

		this.resources = [
			...this.resources.map((resource) => byName.get(resource.name) ?? resource),
			...appended
		];
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

/**
 * The one watcher.
 *
 * A singleton rather than an instance per screen because the screen is a route: only one frame
 * can be under the operator's eye at a time, and `watch()` switching device is exactly what a
 * navigation from one frame to another means.
 */
export const reconcile = new ReconcileState();
