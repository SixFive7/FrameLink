/**
 * Formatting helpers.
 *
 * All time arrives from the server as ISO-8601 UTC (`DateTimeOffset`, camelCased to
 * `lastSeenUtc` and friends) and is rendered in the operator's own zone. The absolute form
 * is always available as a `title`, because "3 minutes ago" is the right thing to read and
 * the wrong thing to put in an incident note.
 */

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto', style: 'long' });

const absolute = new Intl.DateTimeFormat(undefined, {
	dateStyle: 'medium',
	timeStyle: 'medium'
});

const STEPS: Array<[limitSeconds: number, perUnit: number, unit: Intl.RelativeTimeFormatUnit]> = [
	[60, 1, 'second'],
	[3600, 60, 'minute'],
	[86_400, 3600, 'hour'],
	[604_800, 86_400, 'day'],
	[2_629_800, 604_800, 'week'],
	[31_557_600, 2_629_800, 'month'],
	[Number.POSITIVE_INFINITY, 31_557_600, 'year']
];

/** "3 minutes ago", "in 2 days". Under ten seconds reads as "just now". */
export function timeAgo(iso: string, now = Date.now()): string {
	const then = Date.parse(iso);
	if (Number.isNaN(then)) return 'unknown';

	const deltaSeconds = (then - now) / 1000;
	const magnitude = Math.abs(deltaSeconds);
	if (magnitude < 10) return 'just now';

	for (const [limit, perUnit, unit] of STEPS) {
		if (magnitude < limit) {
			return relative.format(Math.round(deltaSeconds / perUnit), unit);
		}
	}
	return 'a long time ago';
}

/** Full local date and time, for titles and detail rows. */
export function timeExact(iso: string): string {
	const parsed = Date.parse(iso);
	return Number.isNaN(parsed) ? iso : absolute.format(parsed);
}

/**
 * A duration in the coarsest unit that still says something: "4 days", "17 minutes".
 * Used for "offline for …" where the *length* of the outage is the point (§3.5).
 */
export function durationSince(iso: string, now = Date.now()): string {
	const then = Date.parse(iso);
	if (Number.isNaN(then)) return 'an unknown time';

	const seconds = Math.max(0, (now - then) / 1000);
	if (seconds < 60) return 'less than a minute';

	const units: Array<[seconds: number, singular: string]> = [
		[31_557_600, 'year'],
		[2_629_800, 'month'],
		[86_400, 'day'],
		[3600, 'hour'],
		[60, 'minute']
	];

	for (const [size, singular] of units) {
		const count = Math.floor(seconds / size);
		if (count >= 1) return `${count} ${singular}${count === 1 ? '' : 's'}`;
	}
	return 'less than a minute';
}

/**
 * Splits a device id into its four groups.
 *
 * The id already arrives hyphenated (`DeviceIdentity.FingerprintOf` renders
 * `XXXX-XXXX-XXXX-XXXX`), but the display component sets the group spacing itself so the
 * hyphens can be rendered as separators rather than as characters — which is what makes the
 * fingerprint readable at 48px from across a bench.
 */
export function fingerprintGroups(deviceId: string): string[] {
	const cleaned = deviceId.replace(/[^0-9A-Za-z]/g, '').toUpperCase();
	if (!cleaned) return [deviceId];
	return cleaned.match(/.{1,4}/g) ?? [cleaned];
}

/** "3 devices" / "1 device". Small thing, but a fleet console says it a lot. */
export function plural(count: number, singular: string, pluralForm = `${singular}s`): string {
	return `${count} ${count === 1 ? singular : pluralForm}`;
}

/** Truncates in the middle, keeping both ends — versions and serials read from both. */
export function ellipsize(value: string, max = 34): string {
	if (value.length <= max) return value;
	const head = Math.ceil((max - 1) / 2);
	const tail = Math.floor((max - 1) / 2);
	return `${value.slice(0, head)}…${value.slice(value.length - tail)}`;
}
