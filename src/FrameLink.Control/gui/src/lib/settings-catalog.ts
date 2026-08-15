/**
 * Presentation metadata for settings keys — and nothing else.
 *
 * version2.md §3.4 is emphatic that the settings model is "not a fixed list but a generic
 * mechanism, because the list will grow", and `ISettingsStore` honours that: a setting is an
 * opaque key and an opaque string value, and the server never validates either.
 *
 * The GUI honours it too. **Every screen renders whatever keys the server actually holds**,
 * and the "add a setting" field accepts any key at all. What lives below is purely additive:
 * when a key the operator is looking at happens to be one this catalog recognises, they get
 * a friendly label, a sentence of help, and an input control suited to the value. When it is
 * not recognised, they get the raw key and a text box, and everything still works.
 *
 * So: adding a key here is a nicety. Forgetting to add one costs nothing. Removing this file
 * entirely would leave a slightly duller but fully functional settings screen. If you ever
 * find yourself wanting to *reject* a key because it is not in this list, stop — that is the
 * exact hard-coding §3.4 rules out.
 *
 * The names below are drawn from the areas §3.4 enumerates: connection values (identity,
 * room, LiveKit, Immich), audio, display, slideshow, locale and time zone, countdown
 * duration and call room.
 */

export type SettingKind = 'text' | 'secret' | 'number' | 'url' | 'duration' | 'boolean' | 'time';

export interface SettingDescriptor {
	/** Human label. Falls back to the raw key when absent. */
	label: string;
	/** One sentence of help, written for someone who has not read the spec. */
	hint: string;
	/** Which input control suits the value. Everything unknown gets `text`. */
	kind: SettingKind;
	/** Grouping heading on the settings screen. Unknown keys land in "Other". */
	group: string;
	/** Shown as the input placeholder. Never a default — the server holds no defaults. */
	example?: string;
}

export const SETTING_GROUPS = [
	'Photos',
	'Calls',
	'Display',
	'Audio',
	'Behaviour',
	'Locale',
	'Other'
] as const;

export const SETTING_CATALOG: Readonly<Record<string, SettingDescriptor>> = {
	'immich.url': {
		label: 'Immich server URL',
		hint: 'Where the photo library lives. Every frame reads its slideshow from here.',
		kind: 'url',
		group: 'Photos',
		example: 'https://immich.example.org'
	},
	'immich.apiKey': {
		label: 'Immich API key',
		hint: 'The credential the slideshow uses to read the library. Stored as given.',
		kind: 'secret',
		group: 'Photos'
	},
	'slideshow.album': {
		label: 'Album',
		hint: 'Which album the frame shows. Per-device is the usual case — each person gets their own.',
		kind: 'text',
		group: 'Photos',
		example: 'Family'
	},
	'slideshow.intervalSeconds': {
		label: 'Photo interval',
		hint: 'Seconds each photo stays on screen before the next one.',
		kind: 'duration',
		group: 'Photos',
		example: '30'
	},
	'call.room': {
		label: 'Call room',
		hint:
			'The room every frame with this value joins. Calling stays single-room and ' +
			'one-button; group calling is just two frames sharing a room name.',
		kind: 'text',
		group: 'Calls',
		example: 'huisman'
	},
	'call.autoAnswer': {
		label: 'Answer automatically',
		hint: 'The viewer never presses anything to be joined. Leave this on.',
		kind: 'boolean',
		group: 'Calls'
	},
	'display.backlightOn': {
		label: 'Screen on at',
		hint: 'Local time the panel wakes up.',
		kind: 'time',
		group: 'Display',
		example: '07:30'
	},
	'display.backlightOff': {
		label: 'Screen off at',
		hint: 'Local time the panel goes dark. Calls still wake it.',
		kind: 'time',
		group: 'Display',
		example: '22:30'
	},
	'display.brightness': {
		label: 'Brightness',
		hint: 'Panel backlight, 0 to 100.',
		kind: 'number',
		group: 'Display',
		example: '80'
	},
	'audio.volume': {
		label: 'Speaker volume',
		hint: 'Playback level for calls, 0 to 100.',
		kind: 'number',
		group: 'Audio',
		example: '75'
	},
	'audio.micGain': {
		label: 'Microphone gain',
		hint: 'Capture level for the mic array, 0 to 100.',
		kind: 'number',
		group: 'Audio'
	},
	'repair.countdownSeconds': {
		label: 'Repair countdown',
		hint:
			'How long a working frame shows what it is about to do before rebooting to verify it. ' +
			'Setting up a new frame never counts down; left unset, a repair pauses for 60 seconds.',
		kind: 'duration',
		group: 'Behaviour',
		example: '60'
	},
	'provisioning.paceSeconds': {
		label: 'Provisioning pace',
		hint:
			'Seconds a frame being set up for the first time pauses before each restart, so you ' +
			'can watch it happen. Leave it at 0 and setting up runs at full speed.',
		kind: 'duration',
		group: 'Behaviour',
		example: '0'
	},
	'update.enabled': {
		label: 'Automatic updates',
		hint: 'Whether the agent converges on the version this server serves. On by default.',
		kind: 'boolean',
		group: 'Behaviour'
	},
	'packages.reportInterval': {
		label: 'Package check interval',
		hint:
			'How often a frame re-reads its own installed software. It only sends anything when ' +
			'something has actually changed, so this is cheap. Six hours if unset.',
		kind: 'duration',
		group: 'Behaviour',
		example: '06:00:00'
	},
	'locale.timezone': {
		label: 'Time zone',
		hint: 'IANA zone name. Drives the backlight schedule and every clock on the frame.',
		kind: 'text',
		group: 'Locale',
		example: 'Europe/Amsterdam'
	},
	'locale.language': {
		label: 'Language',
		hint: 'BCP-47 tag for everything the frame says out loud or on screen.',
		kind: 'text',
		group: 'Locale',
		example: 'nl-NL'
	}
};

/** What is known about a key — real metadata if catalogued, an honest fallback if not. */
export function describeSetting(key: string): SettingDescriptor & { known: boolean } {
	const known = SETTING_CATALOG[key];
	if (known) return { ...known, known: true };

	return {
		label: key,
		hint: 'A setting this Fleet Manager holds but the interface has no description for.',
		kind: key.toLowerCase().includes('secret') || key.toLowerCase().includes('key')
			? 'secret'
			: 'text',
		group: 'Other',
		known: false
	};
}

/** Catalogued keys not yet present on the server — offered as suggestions, never imposed. */
export function suggestedKeys(existing: Iterable<string>): string[] {
	const held = new Set(existing);
	return Object.keys(SETTING_CATALOG)
		.filter((key) => !held.has(key))
		.sort();
}

/** Groups a key set for rendering, keeping `SETTING_GROUPS` order and dropping empty groups. */
export function groupKeys(keys: string[]): Array<{ group: string; keys: string[] }> {
	const buckets = new Map<string, string[]>();
	for (const key of keys) {
		const group = describeSetting(key).group;
		(buckets.get(group) ?? buckets.set(group, []).get(group)!).push(key);
	}

	const ordered: Array<{ group: string; keys: string[] }> = [];
	for (const group of SETTING_GROUPS) {
		const bucket = buckets.get(group);
		if (bucket?.length) ordered.push({ group, keys: bucket.sort() });
		buckets.delete(group);
	}
	// Anything whose group is not in SETTING_GROUPS still renders — the catalog does not get
	// to hide a key the server is holding.
	for (const [group, bucket] of [...buckets].sort(([a], [b]) => a.localeCompare(b))) {
		ordered.push({ group, keys: bucket.sort() });
	}
	return ordered;
}
