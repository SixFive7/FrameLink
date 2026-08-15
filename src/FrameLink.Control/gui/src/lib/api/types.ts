/**
 * The wire shapes, mirroring `ControlContracts.cs` one-for-one.
 *
 * `ControlJson` is a source-generated context with `PropertyNamingPolicy = CamelCase` and
 * `DefaultIgnoreCondition = WhenWritingNull`, so a C# `string?` that is null is *absent* from
 * the JSON rather than present as `null` — which is why every optional field below is
 * declared `?: T` and not `: T | null`.
 *
 * When the server's contracts change, change this file in the same commit. It is the only
 * place in the GUI that is allowed to know what the server calls things.
 */

/** `SetupStatus` — `GET /api/status`. The one endpoint reachable with no session. */
export interface SetupStatus {
	/** False when no usable operator password is configured. */
	configured: boolean;
	/** The environment variable that carries the password, named verbatim. */
	variable: string;
	/** Why the instance is unconfigured, or absent. */
	problem?: string;
	/** A copyable Docker Compose fragment that fixes it. Present only when unconfigured. */
	composeExample?: string;
}

/** `LoginResponse` — `POST /api/session`. */
export interface LoginResponse {
	/** Session token. Also set as an HttpOnly cookie, which is what the GUI actually uses. */
	token: string;
	expiresUtc: string;
}

/** The three adoption states of `DeviceState`, as rendered by `OperatorEndpoints.ToView`. */
export type DeviceState = 'pending' | 'adopted' | 'blocked';

/** `DeviceView` — one row of `GET /api/devices`. */
export interface DeviceView {
	/** Public-key fingerprint, `XXXX-XXXX-XXXX-XXXX` in Crockford Base32. Immutable identity. */
	deviceId: string;
	state: DeviceState;
	/** Whether a socket is open right now. Presence *is* the socket (§3.5). */
	online: boolean;
	/** Operator-assigned name, once adopted. */
	name?: string;
	/** Board serial as claimed by the agent, for bench matching (§3.3). */
	hardwareSerial?: string;
	agentVersion?: string;
	/** The agent's free-text self-report, verbatim. Shown to a person, never parsed — see `health`. */
	agentStatus?: string;
	/**
	 * The same self-report, classified by the server against the vocabulary both programs
	 * share (`AgentHealth`). This is what `presence.ts` reads.
	 */
	health: AgentHealth;
	protocolVersion?: number;
	/** Whether that protocol version is the one this server speaks. */
	protocolCompatible: boolean;
	firstSeenUtc: string;
	/** Most recent proven contact. Reads as "offline since" when not online. */
	lastSeenUtc: string;
	/** When `state` last changed: "adopted on", "blocked since". */
	stateChangedUtc: string;
	/** Address the last proven handshake arrived from. */
	lastRemoteAddress?: string;
}

/**
 * `AgentHealth` — the coarse health the server derives from the agent's free-text self-report.
 *
 * The GUI used to derive this itself by string-matching `agentStatus` against the §2.3
 * vocabulary, which made the browser a second consumer of a field the protocol documents as
 * free text — and got it wrong for every real agent, whose self-report is prose and matched
 * nothing, so a healthy fleet rendered entirely as "Online — degraded". `unknown` is the
 * answer for anything outside the vocabulary, and `unknown` is explicitly not a problem.
 */
export type AgentHealth = 'unknown' | 'in-sync' | 'working' | 'degraded' | 'halted';

/** `DeviceListResponse` — `GET /api/devices`. */
export interface DeviceListResponse {
	devices: DeviceView[];
	includeBlocked: boolean;
}

/** `FleetSettingsResponse` — `GET /api/settings`. */
export interface FleetSettingsResponse {
	revision: number;
	values: Record<string, string>;
}

/** `DeviceSettingsResponse` — `GET /api/devices/{id}/settings`. */
export interface DeviceSettingsResponse {
	deviceId: string;
	revision: number;
	fleetDefaults: Record<string, string>;
	overrides: Record<string, string>;
	/** What the device actually receives. Empty unless the device is adopted. */
	effective: Record<string, string>;
}

/** `ApiError` — every refusal, in a shape the GUI can render. */
export interface ApiErrorBody {
	/** Short machine-readable code: `unauthorized`, `not-configured`, `not-adopted`, … */
	error: string;
	/** Sentence fit to show an operator. */
	detail?: string;
}
