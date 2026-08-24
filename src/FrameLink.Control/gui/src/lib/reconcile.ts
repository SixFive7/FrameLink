/**
 * How reconciliation is worded, coloured and shaped, in one place.
 *
 * Three decisions here carry the whole screen, and all three are about refusing to make
 * something look worse than it is.
 *
 * **1. `blocked` is not a failure.** §2.2 defines it as "this was not attempted, because
 * something it depends on is not in sync" — nothing was tried, no attempt was spent, no reboot
 * happened. A blocked resource is a *consequence*, and painting twelve consequences red beside
 * the one cause that earned it teaches an operator that red means nothing. Blocked is `muted`.
 *
 * **2. `blocked` on `the Fleet Manager` is not even that.** It is the wire spelling of the
 * agent's third observation outcome, `Unevaluable` — the frame could not *ask*, so nothing may
 * be concluded in either direction. `IResource.cs` is emphatic that this "must never become the
 * place a real failure goes to be quiet", and the inverse holds just as hard: rendering it as a
 * fault reports a defect on a frame that behaved perfectly while a server was briefly quiet.
 * It gets `info` and its own wording — *could not ask*, never *observed*.
 *
 * **3. The denominator is resources, never time.** See {@link census}.
 *
 * Everything maps onto the semantic colour roles rather than a primitive ramp, so it is correct
 * in both themes without knowing which one it is in. Same rule as `packages.ts`, and for the
 * same reason: `ahead` is not a fault there, `blocked` is not a fault here.
 */

import type { IconName } from './components/Icon.svelte';
import type { DeviceEventKind, LoopState, ReconcileReport, ResourceReport, ResourceStatus } from './api/types';

export type Tone = 'ok' | 'warn' | 'danger' | 'info' | 'tech' | 'muted';

/**
 * What `blockedBy` says when the frame could not ask rather than when a dependency is broken.
 *
 * `ReconcileLoop.SilentAuthority`, verbatim. It is a `const string` in `FrameLink.Agent` and the
 * protocol never exports it, so this is a hard-coded copy of a value that crosses the wire —
 * the one string in this file that is a contract rather than a label. Every other `blockedBy`
 * is a catalog id naming another row in the same list.
 */
export const SILENT_AUTHORITY = 'the Fleet Manager';

interface Presentation {
	label: string;
	tone: Tone;
	icon: IconName;
	/** One sentence for someone who has not read the spec. */
	meaning: string;
}

const STATUS: Record<ResourceStatus, Presentation> = {
	'in-sync': {
		label: 'In sync',
		tone: 'ok',
		icon: 'check',
		meaning: 'Observed to match, after the setting had to survive a boot.'
	},
	progressing: {
		label: 'Working',
		tone: 'info',
		icon: 'refresh',
		meaning: 'Being acted on right now.'
	},
	'awaiting-reboot': {
		label: 'Awaiting reboot',
		tone: 'warn',
		icon: 'clock',
		meaning:
			'Written, and waiting for the reboot that proves it stuck. §2.4 never claims "applied" ' +
			'from a successful write, so this is the normal path and not a fault.'
	},
	degraded: {
		label: 'Degraded',
		tone: 'danger',
		icon: 'alert',
		meaning: 'The attempt budget ran out. The agent has stopped touching it.'
	},
	blocked: {
		label: 'Waiting',
		tone: 'muted',
		icon: 'minus',
		meaning: 'Never attempted, because something it depends on is not in sync yet.'
	},
	escalated: {
		label: 'Escalated',
		tone: 'danger',
		icon: 'ban',
		meaning:
			'The budget ran out and you have been told. The whole frame has stopped — nothing else ' +
			'is attempted either — until you fix the cause and retry.'
	}
};

const LOOP: Record<LoopState, Presentation> = {
	converged: {
		label: 'Converged',
		tone: 'ok',
		icon: 'check',
		meaning: 'Every resource is verified. There is nothing to do.'
	},
	reconciling: {
		label: 'Reconciling',
		tone: 'info',
		icon: 'refresh',
		meaning: 'A pass is running.'
	},
	'awaiting-reboot': {
		label: 'Awaiting reboot',
		tone: 'warn',
		icon: 'clock',
		meaning: 'A change is written and the verifying reboot is imminent or already in flight.'
	},
	'backing-off': {
		label: 'Backing off',
		tone: 'warn',
		icon: 'clock',
		meaning:
			'Waiting out a per-resource delay before trying again. Backoff exists to stop a reboot ' +
			'loop from wearing the hardware, so a pause here is the design working.'
	},
	escalated: {
		label: 'Escalated',
		tone: 'danger',
		icon: 'ban',
		meaning:
			'At least one resource gave up, so this frame has stopped reconciling entirely and is ' +
			'waiting for a person (§2.5 rungs 4 and 6).'
	}
};

const EVENT: Record<DeviceEventKind, Presentation> = {
	drift: {
		label: 'Drift',
		tone: 'warn',
		icon: 'alert',
		meaning: 'A resource was observed away from its desired value.'
	},
	escalation: {
		label: 'Escalation',
		tone: 'danger',
		icon: 'alert',
		meaning: 'An attempt budget ran out and you were notified.'
	},
	boot: {
		label: 'Boot',
		tone: 'info',
		icon: 'refresh',
		meaning: 'The agent started, naming whether it came back across a reboot boundary.'
	},
	converged: {
		label: 'Converged',
		tone: 'ok',
		icon: 'check',
		meaning: 'Every resource reached in-sync.'
	},
	display: {
		label: 'Dark screen',
		tone: 'warn',
		icon: 'monitor',
		meaning:
			'The frame’s own screen cannot show anything, so this console is the only surface left. ' +
			'Without this event a dark frame is indistinguishable from a working one.'
	},
	'array-firmware': {
		label: 'Microphone firmware',
		tone: 'info',
		icon: 'info',
		meaning:
			'Which firmware the microphone unit is running, reported rather than converged. It is an ' +
			'observation like a boot, not a claim that anything is wrong — a frame on the older ' +
			'firmware runs the product perfectly well.'
	},
	'array-flash': {
		label: 'Microphone write',
		tone: 'warn',
		icon: 'alert',
		meaning:
			'A firmware write to the microphone unit happened, or was refused. Both are the same kind ' +
			'of record: which interlock stopped a frame is as much a part of the trail as a write that ' +
			'went ahead.'
	}
};

/** The fallback for a token this build has not been taught. Never `danger` — see the header. */
const UNKNOWN: Presentation = {
	label: 'Unknown',
	tone: 'tech',
	icon: 'info',
	meaning: 'A status this console does not recognise. The frame is newer than this Fleet Manager.'
};

/**
 * How one resource reads, including the two things `status` alone cannot say.
 *
 * A `blocked` row splits in two here and nowhere else: waiting on a named resource in this
 * frame's own DAG, or waiting on an authority off the device that did not answer. They are the
 * same wire status and they mean very different things to the person reading them.
 */
export function describeResource(resource: ResourceReport): Presentation {
	if (resource.status === 'blocked') {
		return resource.blockedBy === SILENT_AUTHORITY
			? {
					label: 'Could not ask',
					tone: 'info',
					icon: 'info',
					meaning:
						'The frame could not reach the authority that owns this value, so it concluded ' +
						'nothing rather than guessing. No attempt was spent and nothing rebooted; it ' +
						'rechecks on its own.'
				}
			: {
					...STATUS.blocked,
					meaning: resource.blockedBy
						? `Never attempted — it waits on ${resource.blockedBy}, which is not in sync yet.`
						: STATUS.blocked.meaning
				};
	}

	return STATUS[resource.status] ?? UNKNOWN;
}

export function describeLoop(state: LoopState): Presentation {
	return LOOP[state] ?? UNKNOWN;
}

export function describeEvent(kind: DeviceEventKind): Presentation {
	return EVENT[kind] ?? UNKNOWN;
}

/**
 * `currentPhase` in words.
 *
 * Deliberately tolerant. Unlike the status and loop-state vocabularies this one has no name
 * class in the protocol and is documented nowhere; the agent publishes `act` and `reboot` today
 * and null between passes, while §2.3's contract names five phases. So an unrecognised value is
 * shown verbatim rather than mapped to "unknown" — the frame said something and hiding it would
 * be worse than not understanding it.
 */
export function describePhase(phase: string | undefined): string | undefined {
	if (!phase) return undefined;
	if (phase === 'act') return 'applying the change';
	if (phase === 'reboot') return 'rebooting to verify it stuck';
	if (phase === 'observe') return 'reading what is there';
	if (phase === 'verify') return 'checking it survived the boot';
	return phase;
}

/** The statuses that mean the loop has stopped trying, worst first. */
const FAULTED: readonly ResourceStatus[] = ['escalated', 'degraded'];

/**
 * The resources that are the story, worst first.
 *
 * §2.5's ladder is an ordering, so this is one too: `escalated` outranks `degraded`, the two
 * differing only in whether the notification actually reached this server. Everything else on a
 * stopped frame is downstream of these, which is why the screen leads with them instead of putting
 * them in row 41 of a list of 81.
 */
export function faults(resources: readonly ResourceReport[]): ResourceReport[] {
	return resources
		.filter((resource) => FAULTED.includes(resource.status))
		.sort((a, b) => FAULTED.indexOf(a.status) - FAULTED.indexOf(b.status));
}

/** One resource, and everything waiting on it. */
export interface BlockedNode {
	resource: ResourceReport;
	/** Resources blocked directly on this one, each with their own dependents beneath. */
	waiting: BlockedNode[];
}

/**
 * Everything blocked behind `name`, transitively, as a tree.
 *
 * This is the diagnostic the whole screen exists for. `blockedBy` names one resource, so the
 * blocked set is already a forest hanging off whatever broke — and flattening it into a list
 * destroys exactly the relationship an operator needs: *one thing is wrong, and here is
 * everything that is standing still because of it*. §2.2 built that DAG precisely so a
 * dependent would be marked `Blocked(dependency)` "rather than letting them fail confusingly on
 * their own", and a flat list re-creates the confusion the DAG removed.
 *
 * `seen` guards against a cycle. The graph is a DAG by construction and is topologically sorted
 * before the loop runs, so a cycle here would mean a corrupt report — which must render wrong
 * rather than hang the browser.
 */
export function blockedBehind(
	resources: readonly ResourceReport[],
	name: string,
	seen: ReadonlySet<string> = new Set()
): BlockedNode[] {
	if (seen.has(name)) return [];
	const guard = new Set(seen).add(name);

	return resources
		.filter((resource) => resource.status === 'blocked' && resource.blockedBy === name)
		.map((resource) => ({
			resource,
			waiting: blockedBehind(resources, resource.name, guard)
		}));
}

/** How many resources a forest holds, counting every level. */
export function countBlocked(nodes: readonly BlockedNode[]): number {
	return nodes.reduce((total, node) => total + 1 + countBlocked(node.waiting), 0);
}

/**
 * The blocked resources that hang off nothing in this report — a dependency the report did not
 * carry, or the Fleet Manager itself.
 *
 * Grouped by what they wait on so they still read as clusters rather than as a flat tail.
 */
export function orphanedBlocks(
	resources: readonly ResourceReport[],
	claimed: ReadonlySet<string>
): Array<{ blocker: string; waiting: ResourceReport[] }> {
	const groups = new Map<string, ResourceReport[]>();

	for (const resource of resources) {
		if (resource.status !== 'blocked' || claimed.has(resource.name)) continue;
		const blocker = resource.blockedBy ?? 'something unnamed';
		const group = groups.get(blocker);
		if (group) group.push(resource);
		else groups.set(blocker, [resource]);
	}

	return [...groups].map(([blocker, waiting]) => ({ blocker, waiting }));
}

/**
 * The honest denominator.
 *
 * **There is no percentage of time here, and that is the point.** A full provision is roughly
 * half an hour of reboots, arriving one every forty to sixty seconds; a bar that fills smoothly
 * would be inventing a rate it cannot know, and the first time a resource backed off the
 * invention would be a lie. What this returns is a census of *resources*, which is a thing the
 * frame actually counted.
 *
 * `total` is the interesting one. `report.resources` is **not** reliably the whole catalog —
 * the agent publishes a mid-pass report carrying only the resources it has walked so far, and
 * the report it sends immediately before a reboot carries exactly one. But `rebootsExpected` is
 * computed on the frame as `catalogSize - inSync` from the *same* `inSync` in the same message
 * (`ReconcileLoop.PublishReportAsync`), so `inSync + rebootsExpected` recovers the catalog size
 * exactly, in every report, however partial its resource list. That identity is the only stable
 * denominator on the wire and it is why this screen can say "of 78" at all.
 */
export interface Census {
	/** Verified, and proven to have survived a boot. */
	inSync: number;
	/** Every resource this frame's catalog holds. Derived — see above. */
	total: number;
	/** Reboots still expected before convergence. The honest answer to "how long". */
	rebootsExpected: number;
	drifted: number;
	blocked: number;
	/** True when the report's own resource list covers the whole catalog. */
	complete: boolean;
}

export function census(report: ReconcileReport): Census {
	return {
		inSync: report.inSync,
		total: report.inSync + report.rebootsExpected,
		rebootsExpected: report.rebootsExpected,
		drifted: report.drifted,
		blocked: report.blocked,
		complete: report.resources.length >= report.inSync + report.rebootsExpected
	};
}

/** Seconds a verifying reboot cycle takes, as measured — the two ends of the range. */
const REBOOT_SECONDS = [40, 60] as const;

/**
 * How long the remaining reboots will take, as a range and never as a single number.
 *
 * A range is the honest form. The cycle is 40–60 s depending on what the resource touched, and
 * quoting one number would turn a measured spread into a promise the frame never made.
 */
export function rebootTime(rebootsExpected: number): string | undefined {
	if (rebootsExpected <= 0) return undefined;

	const low = Math.round((rebootsExpected * REBOOT_SECONDS[0]) / 60);
	const high = Math.round((rebootsExpected * REBOOT_SECONDS[1]) / 60);

	if (high < 1) return 'under a minute';
	if (low === high) return `about ${low} minute${low === 1 ? '' : 's'}`;
	return `roughly ${low} to ${high} minutes`;
}

/** "Attempt 3 of 5", or just "attempt 3" when the frame did not say what the budget was. */
export function attemptLabel(resource: ResourceReport): string | undefined {
	if (resource.attempts <= 0) return undefined;
	// `attemptBudget` is a plain int and always serialises, so zero means "never set" as well as
	// "zero" — and "attempt 3 of 0" is worse than saying nothing about the budget.
	return resource.attemptBudget > 0
		? `attempt ${resource.attempts} of ${resource.attemptBudget}`
		: `attempt ${resource.attempts}`;
}
