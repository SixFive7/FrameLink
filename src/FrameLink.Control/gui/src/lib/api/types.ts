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

/**
 * How one package stands against the reviewed baseline.
 *
 * `ahead` is the *expected* value and is never styled as a fault: a frame behind NAT is left
 * running Debian's security-only automatic updates, so its packages are supposed to move
 * forward on their own. `behind` and `missing` are the two that mean something is wrong.
 */
export type PackageStatus = 'same' | 'ahead' | 'behind' | 'missing' | 'extra';

/** What can happen to one package between two reports. */
export type PackageChangeKind = 'installed' | 'removed' | 'upgraded' | 'downgraded';

/** `PackageDeltaView` — one package's standing against the baseline. */
export interface PackageDelta {
	package: string;
	status: PackageStatus;
	/** The reviewed version. Absent when the baseline never named this package. */
	baseline?: string;
	/** What this frame has. Absent when it does not have it. */
	installed?: string;
}

/** `PackageSummaryView` — one frame's package standing, in five numbers. */
export interface PackageSummary {
	deviceId: string;
	name?: string;
	online: boolean;
	observedUtc: string;
	/** The key the set is stored under. Two frames sharing one are byte-identical. */
	contentHash: string;
	installed: number;
	ahead: number;
	behind: number;
	missing: number;
	extra: number;
}

/** `PackageVersionGroupView` — one version, and the frames on it. */
export interface PackageVersionGroup {
	/** Absent when this group is the frames that do not have the package at all. */
	version?: string;
	deviceIds: string[];
}

/** `PackageDisagreementView` — one package the fleet does not agree on. */
export interface PackageDisagreement {
	package: string;
	baseline?: string;
	versions: PackageVersionGroup[];
}

/** `FleetPackagesResponse` — `GET /api/packages`. */
export interface FleetPackagesResponse {
	devices: PackageSummary[];
	/** Packages every reporting frame has at the same version. */
	agreed: number;
	disagreementTotal: number;
	disagreements: PackageDisagreement[];
	/** One means the whole fleet is byte-identical. */
	distinctSets: number;
	baselineCount: number;
	baselineReviewedUtc: string;
}

/** `PackageChangeView` — one package that moved. */
export interface PackageChange {
	package: string;
	change: PackageChangeKind;
	from?: string;
	to?: string;
}

/** `PackageChangeSetView` — everything that moved on one frame at one moment. */
export interface PackageChangeSet {
	observedUtc: string;
	total: number;
	changes: PackageChange[];
}

/** `DevicePackagesResponse` — `GET /api/devices/{id}/packages`. */
export interface DevicePackagesResponse {
	deviceId: string;
	online: boolean;
	/** Absent when this frame has never reported an inventory. */
	summary?: PackageSummary;
	observedCount: number;
	driftTotal: number;
	drift: PackageDelta[];
	recent: PackageChangeSet[];
	baselineCount: number;
	baselineReviewedUtc: string;
}

/** `ApiError` — every refusal, in a shape the GUI can render. */
export interface ApiErrorBody {
	/** Short machine-readable code: `unauthorized`, `not-configured`, `not-adopted`, … */
	error: string;
	/** Sentence fit to show an operator. */
	detail?: string;
}
