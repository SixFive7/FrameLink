<script lang="ts">
	/**
	 * The §3.5 presence ladder as a chip.
	 *
	 * The wording comes from `presence.ts` — the labels are §3.5's own names for the states,
	 * not paraphrases — and the offline case appends the "offline since" the spec asks for.
	 * Hovering gives the one-line meaning, so an operator who has not read version2.md can
	 * still tell `Incompatible` from `Offline`.
	 */
	import type { DeviceView } from '$lib/api/types';
	import { describePresence } from '$lib/presence';
	import { durationSince, timeExact } from '$lib/format';
	import Chip from './Chip.svelte';

	interface Props {
		device: DeviceView;
		size?: 'sm' | 'md';
		/** Appends "offline for 4 days" inline instead of only in the tooltip. */
		verbose?: boolean;
	}

	let { device, size = 'md', verbose = false }: Props = $props();

	const info = $derived(describePresence(device));
	const offlineFor = $derived(
		info.presence === 'offline' ? durationSince(device.lastSeenUtc) : undefined
	);

	const title = $derived(
		offlineFor
			? `${info.meaning}\nLast contact ${timeExact(device.lastSeenUtc)}`
			: info.meaning
	);
</script>

<Chip tone={info.tone} dot pulse={info.presence === 'online'} {size} {title}>
	{info.label}{#if verbose && offlineFor}<span class="since"> · {offlineFor}</span>{/if}
</Chip>

<style>
	.since {
		font-weight: var(--weight-normal);
		opacity: 0.75;
	}
</style>
