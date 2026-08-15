/**
 * How package drift is worded and coloured, in one place.
 *
 * The single most important decision in this file is that **`ahead` is not a fault**. A frame
 * behind NAT with no inbound port is deliberately left running Debian's security-only automatic
 * updates, so its packages are *supposed* to move forward on their own; colouring that red would
 * train an operator to ignore the one colour that matters. `behind` and `missing` are the two
 * that mean something is wrong, and they are the two that get `danger`.
 *
 * Everything here maps onto the semantic colour roles rather than a primitive ramp, so it is
 * correct in both themes without knowing which one it is in.
 */

import type { IconName } from './components/Icon.svelte';
import type { PackageChangeKind, PackageStatus } from './api/types';

export type Tone = 'ok' | 'warn' | 'danger' | 'info' | 'tech' | 'muted';

interface Presentation {
	label: string;
	tone: Tone;
	icon: IconName;
	/** One sentence for someone who has not read the spec. */
	meaning: string;
}

const STATUS: Record<PackageStatus, Presentation> = {
	same: {
		label: 'Matches',
		tone: 'ok',
		icon: 'check',
		meaning: 'Exactly the version that was reviewed.'
	},
	ahead: {
		label: 'Newer',
		tone: 'info',
		icon: 'plus',
		meaning:
			'Newer than the reviewed version. This is what a security update looks like — expected, ' +
			'reported, and never undone.'
	},
	behind: {
		label: 'Older',
		tone: 'danger',
		icon: 'alert',
		meaning:
			'Older than the reviewed version. Nothing a frame does on its own moves a package ' +
			'backwards, so this one needs looking at.'
	},
	missing: {
		label: 'Missing',
		tone: 'danger',
		icon: 'minus',
		meaning: 'The reviewed set has this package and this frame does not.'
	},
	extra: {
		label: 'Extra',
		tone: 'tech',
		icon: 'plus',
		meaning: 'This frame has a package the reviewed set never named.'
	}
};

const CHANGE: Record<PackageChangeKind, Presentation> = {
	installed: {
		label: 'Installed',
		tone: 'ok',
		icon: 'plus',
		meaning: 'The package appeared on this frame.'
	},
	removed: {
		label: 'Removed',
		tone: 'warn',
		icon: 'minus',
		meaning: 'The package went away.'
	},
	upgraded: {
		label: 'Upgraded',
		tone: 'info',
		icon: 'check',
		meaning: 'The package moved forward.'
	},
	downgraded: {
		label: 'Downgraded',
		tone: 'danger',
		icon: 'alert',
		meaning: 'The package moved backwards, which nothing normal does.'
	}
};

export function describeStatus(status: PackageStatus): Presentation {
	return STATUS[status] ?? STATUS.extra;
}

export function describeChange(change: PackageChangeKind): Presentation {
	return CHANGE[change] ?? CHANGE.upgraded;
}

/**
 * The one number that says whether a frame needs attention.
 *
 * Deliberately not the sum of all four. An operator scanning a fleet list wants to know which
 * rows are wrong, and being ahead on forty packages is not wrong — it is a frame that took its
 * updates. Missing and behind are.
 */
export function faultCount(summary: { behind: number; missing: number }): number {
	return summary.behind + summary.missing;
}

/** A short version pair for a row: "0.9.2-1+rpt4 → 0.9.3-1". */
export function versionArrow(from: string | undefined, to: string | undefined): string {
	if (from && to) return `${from} → ${to}`;
	return to ?? from ?? '—';
}
