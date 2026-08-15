/**
 * The one place the GUI talks to `fl-control`.
 *
 * Session handling is entirely cookie-based. `POST /api/session` sets an HttpOnly,
 * SameSite=Strict cookie (`OperatorEndpoints.SignIn`), which the browser then attaches to
 * every same-origin request without the GUI touching it. The login response also carries a
 * bearer token; it is deliberately ignored here, because storing it in JS would give an XSS
 * a credential that the HttpOnly cookie denies it.
 *
 * Every non-2xx answer becomes an `ApiError` carrying the server's own `error` code and
 * `detail` sentence. Those sentences were written to be shown to an operator
 * (`ControlContracts.ApiError`: "Sentence fit to show an operator"), so the GUI shows them
 * rather than inventing its own.
 */

import type {
	DeviceListResponse,
	DeviceSettingsResponse,
	DeviceView,
	FleetSettingsResponse,
	LoginResponse,
	SetupStatus
} from './types';

/** A refusal from the server, or a network failure dressed as one. */
export class ApiError extends Error {
	readonly status: number;
	/** The server's machine-readable code, or `network` / `unknown` when there was no body. */
	readonly code: string;

	constructor(status: number, code: string, detail: string) {
		super(detail);
		this.name = 'ApiError';
		this.status = status;
		this.code = code;
	}

	/** The session is gone or was never there. The layout guard routes to /login on this. */
	get isUnauthorized(): boolean {
		return this.status === 401;
	}

	/** The server has no operator password. Everything routes to /setup on this. */
	get isNotConfigured(): boolean {
		return this.code === 'not-configured';
	}

	/** A settings write against a device that is not adopted (409, §3.4). */
	get isNotAdopted(): boolean {
		return this.code === 'not-adopted';
	}
}

/**
 * Called whenever a request comes back 401. The session store installs itself here so an
 * expired session anywhere in the app lands the operator on the login screen once, rather
 * than every screen inventing its own recovery.
 */
let onUnauthorized: (() => void) | undefined;

export function handleUnauthorized(handler: () => void) {
	onUnauthorized = handler;
}

interface RequestOptions {
	method?: string;
	body?: unknown;
	signal?: AbortSignal;
	/** Suppresses the global 401 handler — used by the bootstrap probe, whose whole job is
	    to find out whether there is a session. */
	quiet?: boolean;
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
	const { method = 'GET', body, signal, quiet } = options;

	let response: Response;
	try {
		response = await fetch(path, {
			method,
			signal,
			// The cookie is HttpOnly and SameSite=Strict; same-origin is all it needs and all
			// it should get.
			credentials: 'same-origin',
			headers: body === undefined ? undefined : { 'content-type': 'application/json' },
			body: body === undefined ? undefined : JSON.stringify(body)
		});
	} catch (cause) {
		if (signal?.aborted) throw cause;
		throw new ApiError(0, 'network', 'The Fleet Manager did not answer. It may be restarting.');
	}

	if (response.status === 401 && !quiet) {
		onUnauthorized?.();
	}

	if (!response.ok) {
		throw new ApiError(response.status, ...(await readError(response)));
	}

	if (response.status === 204) return undefined as T;

	const text = await response.text();
	return (text ? JSON.parse(text) : undefined) as T;
}

async function readError(response: Response): Promise<[code: string, detail: string]> {
	try {
		const body = (await response.json()) as { error?: string; detail?: string };
		if (body?.error) {
			return [body.error, body.detail ?? defaultDetail(response.status)];
		}
	} catch {
		/* not JSON — fall through to the status-based sentence */
	}
	return ['unknown', defaultDetail(response.status)];
}

function defaultDetail(status: number): string {
	if (status === 401) return 'Sign in with the operator password.';
	if (status === 404) return 'That is no longer here.';
	if (status >= 500) return 'The Fleet Manager failed to answer that.';
	return `The Fleet Manager refused that request (HTTP ${status}).`;
}

/**
 * The operator API, one function per route.
 *
 * `/agent/*` is deliberately absent: it is the device path, authenticated by keypair, and no
 * part of the GUI has any business there (`OperatorGate` exempts it structurally).
 */
export const api = {
	/** `GET /api/status` — reachable without a session, by design (§3.2). */
	status: (signal?: AbortSignal) => request<SetupStatus>('/api/status', { signal, quiet: true }),

	/** `POST /api/session` — 401 on a wrong password, 503 `not-configured` when there is none. */
	signIn: (password: string) =>
		request<LoginResponse>('/api/session', { method: 'POST', body: { password }, quiet: true }),

	/** `DELETE /api/session`. */
	signOut: () => request<void>('/api/session', { method: 'DELETE', quiet: true }),

	/**
	 * `GET /api/devices` — blocked rows are filtered out unless asked for, so an accidental
	 * block stays reversible (§3.3).
	 */
	devices: (includeBlocked: boolean, options: { signal?: AbortSignal; quiet?: boolean } = {}) =>
		request<DeviceListResponse>(`/api/devices?includeBlocked=${includeBlocked}`, options),

	/**
	 * `GET /api/devices/{id}` — one device, whatever state it is in.
	 *
	 * The detail screen used to take its row out of the polled fleet list, so a hard page load
	 * showed a placeholder until the next poll landed, and a blocked device's page only worked
	 * because the list was always fetched with `includeBlocked=true`.
	 */
	device: (deviceId: string, options: { signal?: AbortSignal } = {}) =>
		request<DeviceView>(`/api/devices/${encodeURIComponent(deviceId)}`, options),

	/**
	 * `POST /api/devices/{id}/adopt` — no body, and the optional name rides in the query.
	 *
	 * Also the rename route: adopting an already-adopted device writes the new name. It refuses
	 * a *blocked* device with 409 `blocked`, because unblocking is what returns a frame to the
	 * adoption queue and re-trusting it is a second, deliberate press (§3.3).
	 */
	adopt: (deviceId: string, name?: string) => {
		const trimmed = name?.trim();
		const query = trimmed ? `?name=${encodeURIComponent(trimmed)}` : '';
		return request<DeviceView>(
			`/api/devices/${encodeURIComponent(deviceId)}/adopt${query}`,
			{ method: 'POST' }
		);
	},

	block: (deviceId: string) =>
		request<DeviceView>(`/api/devices/${encodeURIComponent(deviceId)}/block`, { method: 'POST' }),

	/** Returns the device to *pending*, not to adopted — trusting it again is a second press. */
	unblock: (deviceId: string) =>
		request<DeviceView>(`/api/devices/${encodeURIComponent(deviceId)}/unblock`, { method: 'POST' }),

	/** `DELETE /api/devices/{id}` — forgets the row entirely. The device reappears as pending. */
	forget: (deviceId: string) =>
		request<void>(`/api/devices/${encodeURIComponent(deviceId)}`, { method: 'DELETE' }),

	fleetSettings: (signal?: AbortSignal) => request<FleetSettingsResponse>('/api/settings', { signal }),

	setFleetSetting: (key: string, value: string) =>
		request<void>(`/api/settings/${encodeURIComponent(key)}`, { method: 'PUT', body: { value } }),

	removeFleetSetting: (key: string) =>
		request<void>(`/api/settings/${encodeURIComponent(key)}`, { method: 'DELETE' }),

	deviceSettings: (deviceId: string, signal?: AbortSignal) =>
		request<DeviceSettingsResponse>(`/api/devices/${encodeURIComponent(deviceId)}/settings`, {
			signal
		}),

	/** 409 `not-adopted` when the device is pending or blocked — surfaced, not swallowed. */
	setDeviceSetting: (deviceId: string, key: string, value: string) =>
		request<void>(
			`/api/devices/${encodeURIComponent(deviceId)}/settings/${encodeURIComponent(key)}`,
			{ method: 'PUT', body: { value } }
		),

	removeDeviceSetting: (deviceId: string, key: string) =>
		request<void>(
			`/api/devices/${encodeURIComponent(deviceId)}/settings/${encodeURIComponent(key)}`,
			{ method: 'DELETE' }
		)
};
