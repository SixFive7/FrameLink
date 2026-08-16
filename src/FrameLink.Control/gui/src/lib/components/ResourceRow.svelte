<script lang="ts">
	/**
	 * One resource's standing, as one row.
	 *
	 * The same row is used in three places — the blast radius under a fault, the waiting
	 * clusters, and the full catalog list — because they are the same object seen from different
	 * angles, and a screen that drew three different rows for it would be inventing three
	 * different things to learn.
	 *
	 * The tinted left edge carries the status, the way `PackagePanel` does it: cheaper to scan
	 * than a full-row tint, and still readable when seventy of them stack. What it never carries
	 * is a red edge for a resource that merely waited — see `$lib/reconcile`.
	 */
	import { attemptLabel, describeResource } from '$lib/reconcile';
	import type { ResourceReport } from '$lib/api/types';
	import { timeAgo, timeExact } from '$lib/format';
	import Icon from '$lib/components/Icon.svelte';

	interface Props {
		resource: ResourceReport;
		/** Marks the resource the loop is working on this instant. */
		current?: boolean;
		/** Hides the delta, for the dense full-catalog list where it would swamp the names. */
		terse?: boolean;
	}

	let { resource, current = false, terse = false }: Props = $props();

	const look = $derived(describeResource(resource));
	const attempt = $derived(attemptLabel(resource));
</script>

<div class="row {look.tone}" class:current>
	<span class="badge" title={look.meaning}>
		<Icon name={look.icon} size={12} />
		{look.label}
	</span>

	<div class="body">
		<code class="name">{resource.name}</code>
		{#if !terse && resource.delta}
			<p class="delta">{resource.delta}</p>
		{/if}
		{#if !terse && resource.action}
			<p class="action" title="The exact change the agent last made">{resource.action}</p>
		{/if}
	</div>

	<span class="meta">
		{#if current}
			<span class="live">working now</span>
		{/if}
		{#if attempt}
			<span>{attempt}</span>
		{/if}
		{#if resource.escalations > 0}
			<span class="escalations">
				notified {resource.escalations}×
			</span>
		{/if}
		{#if resource.nextAttemptUtc}
			<span title={timeExact(resource.nextAttemptUtc)}>
				retries {timeAgo(resource.nextAttemptUtc)}
			</span>
		{/if}
	</span>
</div>

<style>
	.row {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		padding: var(--space-2) var(--space-3);
		border-radius: var(--radius-xs);
		border-left: 2px solid var(--line);
		background: var(--surface-1);
		font-size: var(--text-xs);
		flex-wrap: wrap;
		transition:
			background-color var(--dur-base) var(--ease-standard),
			border-left-color var(--dur-base) var(--ease-standard);
	}

	.row.danger {
		border-left-color: var(--danger);
	}
	.row.warn {
		border-left-color: var(--accent);
	}
	.row.info {
		border-left-color: var(--info);
	}
	.row.ok {
		border-left-color: var(--ok);
	}
	.row.tech {
		border-left-color: var(--tech);
	}

	/* `muted` keeps the neutral hairline it inherits. A resource that was never attempted has
	   nothing to report, and giving it a colour would make it compete with the thing that does. */

	.row.current {
		background: var(--info-soft);
		border-left-color: var(--info);
	}

	.badge {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		min-width: 7.5rem;
		padding-top: 1px;
		font-size: var(--text-2xs);
		letter-spacing: var(--track-wide);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.row.danger .badge {
		color: var(--danger);
	}
	.row.warn .badge {
		color: var(--accent);
	}
	.row.info .badge,
	.row.current .badge {
		color: var(--info);
	}
	.row.ok .badge {
		color: var(--ok);
	}
	.row.tech .badge {
		color: var(--tech);
	}

	.body {
		display: grid;
		gap: 2px;
		flex: 1 1 16rem;
		min-width: 0;
	}

	.name {
		font-family: var(--font-mono);
		color: var(--text-1);
		overflow-wrap: anywhere;
	}

	.delta {
		color: var(--text-2);
		line-height: var(--leading-snug);
		overflow-wrap: anywhere;
	}

	.action {
		font-family: var(--font-mono);
		font-size: var(--text-2xs);
		color: var(--text-3);
		overflow-wrap: anywhere;
	}

	.meta {
		display: inline-flex;
		align-items: center;
		gap: var(--space-3);
		flex-wrap: wrap;
		color: var(--text-3);
		font-variant-numeric: tabular-nums;
	}

	.live {
		color: var(--info);
		font-weight: var(--weight-semibold);
	}

	.escalations {
		color: var(--danger);
	}
</style>
