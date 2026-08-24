/**
 * How a microphone-firmware write is worded and coloured, in one place.
 *
 * Same rule as `reconcile.ts` and `packages.ts`: nothing is painted worse than it is, and
 * nothing is painted better. Two applications of that here, and both are deliberate.
 *
 * **A frame on the older firmware is not a fault.** It runs the product perfectly well — the
 * amplifier is on at boot on 2.0.6 and on 2.1.0 alike, measured on the bench — so "not on the
 * pinned firmware" is `info`, never `warn`. Decision 90 removed a resource precisely so that a
 * working frame would never be stopped over a number, and colouring it red here would put the
 * pressure back that the removal took out.
 *
 * **A refusal is not an error.** Every value in the agent's `ArrayFlashRefusal` is an interlock
 * doing its job; most of them are the ordinary state of a frame nobody has authorised. Two are
 * different in kind and get `danger` — a write that did not produce the pinned firmware, and a
 * previous write that never finished — because both mean somebody has to go and look at a unit.
 *
 * The refusal names below are the agent's enum spellings. A name this build has not been taught
 * is shown as it arrived rather than hidden, exactly as an unrecognised resource status is.
 */

import type { IconName } from './components/Icon.svelte';
import type { ArrayFlashPhase, ArrayFlashStatusResponse } from './api/types';
import type { Tone } from './reconcile';

export interface FlashPresentation {
	label: string;
	tone: Tone;
	icon: IconName;
}

const PHASE: Record<ArrayFlashPhase, FlashPresentation> = {
	'not-authorised': { label: 'Not authorised', tone: 'muted', icon: 'shieldCheck' },
	authorised: { label: 'Authorised', tone: 'warn', icon: 'key' },
	'awaiting-household': { label: 'Waiting for somebody at the frame', tone: 'warn', icon: 'clock' },
	refused: { label: 'Refused', tone: 'info', icon: 'ban' },
	flashed: { label: 'Written', tone: 'ok', icon: 'check' },
	failed: { label: 'Write failed', tone: 'danger', icon: 'alert' }
};

const UNKNOWN: FlashPresentation = { label: 'Unknown', tone: 'tech', icon: 'info' };

/**
 * The refusals that mean a person has to go and put hands on a unit, rather than that an
 * interlock did its job and the frame is fine.
 */
const HANDS_ON = new Set(['PreviousFlashUnfinished']);

/** How the current phase reads. */
export function describeFlashPhase(view: ArrayFlashStatusResponse): FlashPresentation {
	const look = PHASE[view.phase] ?? UNKNOWN;

	return view.refusal && HANDS_ON.has(view.refusal)
		? { ...look, label: 'Needs somebody at the frame', tone: 'danger', icon: 'alert' }
		: look;
}

/**
 * The interlock's name as a person would say it, or the raw token when this build has not been
 * taught it.
 *
 * Only the name is translated. The sentence beside it is always the frame's own `detail`, which
 * says what to do about this particular refusal on this particular frame — a second, staler
 * explanation written here would be the drift `reconcile.ts` warns about in its own header.
 */
export function describeRefusal(refusal: string): string {
	switch (refusal) {
		case 'NotAuthorised':
			return 'Nobody has authorised one';
		case 'AlreadyConsumed':
			return 'This authorisation has already been spent';
		case 'NotThePinnedImage':
			return 'The authorisation names a different image';
		case 'ImageNotVerified':
			return 'The image on this frame is missing or does not match';
		case 'RecoveryNotVerified':
			return 'There is no verified way back on this frame';
		case 'DfuUtilMissing':
			return 'The writing program is not installed';
		case 'NoArrayAttached':
			return 'No microphone unit is on the bus';
		case 'MoreThanOneArray':
			return 'More than one microphone unit is on the bus';
		case 'AlreadyAtTarget':
			return 'It already runs the pinned firmware';
		case 'PreviousFlashUnfinished':
			return 'A previous write never finished';
		case 'CallInProgress':
			return 'Somebody is on a call';
		case 'AgentRestartPending':
			return 'The agent is about to restart';
		case 'AwaitingLocalApproval':
			return 'Nobody at the frame has agreed yet';
		case 'ArrayNotRecognised':
			return 'The unit on the bus is not one this build knows';
		default:
			return refusal;
	}
}

/** `2 1 0` as the array spells it, `2.1.0` as a person reads it. */
export function firmwareVersion(version: string): string {
	return version.trim().split(/\s+/).join('.');
}

/** `60fee566…ca7b78d6`, which is enough to compare two digests by eye. */
export function shortDigest(sha256: string): string {
	return sha256.length <= 20 ? sha256 : `${sha256.slice(0, 12)}…${sha256.slice(-8)}`;
}
