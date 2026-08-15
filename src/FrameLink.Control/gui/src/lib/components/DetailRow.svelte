<script lang="ts">
	/**
	 * One label/value pair on the device detail screen.
	 *
	 * Labels are uppercase micro-type on `--track-caps`; values are full-size and selectable.
	 * A missing value renders as an em dash in the muted tone rather than as an empty cell —
	 * "the agent has not told us its serial" and "the serial is blank" must look different.
	 */
	import type { Snippet } from 'svelte';

	interface Props {
		label: string;
		value?: string;
		mono?: boolean;
		title?: string;
		children?: Snippet;
	}

	let { label, value, mono = false, title, children }: Props = $props();
</script>

<div class="row">
	<dt>{label}</dt>
	<dd class:mono class:absent={!children && !value} {title}>
		{#if children}
			{@render children()}
		{:else if value}
			{value}
		{:else}
			<span aria-label="not reported">—</span>
		{/if}
	</dd>
</div>

<style>
	.row {
		display: grid;
		gap: var(--space-1);
		padding: var(--space-3) 0;
		border-bottom: 1px solid var(--line);
	}

	.row:last-child {
		border-bottom: 0;
	}

	dt {
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	dd {
		margin: 0;
		font-size: var(--text-sm);
		color: var(--text-1);
		overflow-wrap: anywhere;
	}

	.mono {
		font-family: var(--font-mono);
		font-size: var(--text-xs);
		letter-spacing: var(--track-code);
	}

	.absent {
		color: var(--text-3);
	}
</style>
