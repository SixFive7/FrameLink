<script lang="ts">
	/**
	 * One frame's packages, on its own page.
	 *
	 * Two questions and no third: **how does this frame stand** and **what changed on it
	 * recently**. Neither is answered by a list of ~930 rows, so neither is rendered as one —
	 * the server sends the differences and the counts, and this draws them.
	 *
	 * The colour rule is the whole design and it is worth restating where it is applied:
	 * *newer* is `info`, never `danger`. A frame ahead of the reviewed baseline has taken the
	 * security updates it is supposed to take, and painting that red would teach an operator to
	 * ignore red. Older and missing are the two that mean something is wrong.
	 */
	import { onMount } from 'svelte';
	import { api, ApiError } from '$lib/api/client';
	import type { DevicePackagesResponse } from '$lib/api/types';
	import { describeChange, describeStatus, faultCount } from '$lib/packages';
	import { plural, timeAgo, timeExact } from '$lib/format';
	import { collapse } from '$lib/design/motion';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import Icon from '$lib/components/Icon.svelte';

	interface Props {
		deviceId: string;
	}

	let { deviceId }: Props = $props();

	let view = $state<DevicePackagesResponse | undefined>();
	let problem = $state<string | undefined>();
	let loading = $state(true);

	/** How many drift rows are drawn before the rest are folded away. */
	const FIRST_ROWS = 12;
	let expanded = $state(false);

	const summary = $derived(view?.summary);
	const faults = $derived(summary ? faultCount(summary) : 0);
	const shown = $derived(expanded ? (view?.drift ?? []) : (view?.drift ?? []).slice(0, FIRST_ROWS));
	const truncated = $derived(
		view !== undefined && view.drift.length < view.driftTotal
	);

	async function load() {
		try {
			view = await api.devicePackages(deviceId);
			problem = undefined;
		} catch (cause) {
			problem =
				cause instanceof ApiError ? cause.message : 'This frame’s packages could not be read.';
		} finally {
			loading = false;
		}
	}

	onMount(load);
</script>

<Card>
	<div class="head">
		<h2>Packages</h2>
		{#if summary}
			<Chip tone={faults > 0 ? 'danger' : 'ok'} size="sm" icon={faults > 0 ? 'alert' : 'check'}>
				{faults > 0 ? plural(faults, 'needs attention', 'need attention') : 'nothing wrong'}
			</Chip>
			<Chip tone="muted" size="sm" title={timeExact(summary.observedUtc)}>
				read {timeAgo(summary.observedUtc)}
			</Chip>
		{/if}
	</div>

	{#if problem}
		<p class="problem">{problem}</p>
	{:else if loading}
		<p class="muted">Reading…</p>
	{:else if !summary}
		<p class="muted">
			This frame has not reported its packages yet. It sends the whole list the first time it
			comes up, and again whenever anything changes.
		</p>
	{:else}
		<div class="stats">
			<div class="stat">
				<b>{summary.installed}</b>
				<small>installed</small>
			</div>
			<div class="stat info" class:zero={summary.ahead === 0}>
				<b>{summary.ahead}</b>
				<small>newer</small>
			</div>
			<div class="stat danger" class:zero={summary.behind === 0}>
				<b>{summary.behind}</b>
				<small>older</small>
			</div>
			<div class="stat danger" class:zero={summary.missing === 0}>
				<b>{summary.missing}</b>
				<small>missing</small>
			</div>
			<div class="stat tech" class:zero={summary.extra === 0}>
				<b>{summary.extra}</b>
				<small>extra</small>
			</div>
		</div>

		<p class="note">
			Measured against the {view?.baselineCount} packages reviewed
			<span title={timeExact(view?.baselineReviewedUtc ?? '')}
				>{timeAgo(view?.baselineReviewedUtc ?? '')}</span
			>. {describeStatus('ahead').meaning}
		</p>

		{#if view && view.observedCount > summary.installed}
			<p class="note warn">
				<Icon name="alert" size={14} />
				This frame reported {view.observedCount} installed packages, more than one message can
				carry, so {summary.installed} of them are named here.
			</p>
		{/if}

		{#if shown.length > 0}
			<h3>Against the reviewed set</h3>
			<div class="rows">
				{#each shown as row (row.package)}
					{@const look = describeStatus(row.status)}
					<div class="row {look.tone}">
						<span class="badge" title={look.meaning}>
							<Icon name={look.icon} size={12} />
							{look.label}
						</span>
						<code class="name">{row.package}</code>
						<span class="versions">
							{#if row.baseline}<code class="was">{row.baseline}</code>{/if}
							{#if row.baseline && row.installed}<Icon name="chevronRight" size={12} />{/if}
							{#if row.installed}<code class="is">{row.installed}</code>{/if}
						</span>
					</div>
				{/each}
			</div>

			{#if (view?.drift.length ?? 0) > FIRST_ROWS}
				<button class="more" onclick={() => (expanded = !expanded)}>
					{expanded
						? 'Show fewer'
						: `Show all ${view?.drift.length} differences${truncated ? ` of ${view?.driftTotal}` : ''}`}
				</button>
			{:else if truncated}
				<p class="note">Showing {view?.drift.length} of {view?.driftTotal} differences.</p>
			{/if}
		{/if}

		{#if view && view.recent.length > 0}
			<h3>What changed here</h3>
			<ol class="timeline">
				{#each view.recent as moment (moment.observedUtc)}
					<li transition:collapse>
						<div class="when" title={timeExact(moment.observedUtc)}>
							{timeAgo(moment.observedUtc)}
							<span class="count">{plural(moment.total, 'package')}</span>
						</div>
						<div class="moves">
							{#each moment.changes.slice(0, 8) as move (move.package)}
								{@const look = describeChange(move.change)}
								<div class="move {look.tone}">
									<span class="badge" title={look.meaning}>
										<Icon name={look.icon} size={12} />
										{look.label}
									</span>
									<code class="name">{move.package}</code>
									<span class="versions">
										{#if move.from}<code class="was">{move.from}</code>{/if}
										{#if move.from && move.to}<Icon name="chevronRight" size={12} />{/if}
										{#if move.to}<code class="is">{move.to}</code>{/if}
									</span>
								</div>
							{/each}
							{#if moment.changes.length > 8}
								<p class="note">and {moment.changes.length - 8} more.</p>
							{/if}
						</div>
					</li>
				{/each}
			</ol>
		{:else if summary}
			<h3>What changed here</h3>
			<p class="muted">
				Nothing has moved since this frame first reported. A frame only reports when its
				packages actually change, so an empty history is a quiet frame rather than a missing
				one.
			</p>
		{/if}
	{/if}
</Card>

<style>
	.head {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		margin-bottom: var(--space-4);
		flex-wrap: wrap;
	}

	.head h2 {
		font-size: var(--text-lg);
		margin-right: auto;
	}

	h3 {
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
		margin: var(--space-6) 0 var(--space-3);
	}

	.stats {
		display: flex;
		gap: var(--space-6);
		flex-wrap: wrap;
		padding: var(--space-4) 0 var(--space-2);
	}

	.stat {
		display: grid;
		gap: 2px;
	}

	.stat b {
		font-family: var(--font-mono);
		font-size: var(--text-xl);
		font-weight: var(--weight-semibold);
		font-variant-numeric: tabular-nums;
		line-height: 1;
	}

	.stat small {
		font-size: var(--text-2xs);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.stat.info b {
		color: var(--info);
	}
	.stat.danger b {
		color: var(--danger);
	}
	.stat.tech b {
		color: var(--tech);
	}

	/* A zero is not news, so it stops competing for the eye. */
	.stat.zero b {
		color: var(--text-3);
		font-weight: var(--weight-normal);
	}

	.note {
		color: var(--text-3);
		font-size: var(--text-xs);
		line-height: var(--leading-normal);
		max-width: var(--width-prose);
	}

	.note.warn {
		display: flex;
		align-items: flex-start;
		gap: var(--space-2);
		margin-top: var(--space-3);
		color: var(--accent);
	}

	.note.warn :global(.icon) {
		flex: none;
		margin-top: 2px;
	}

	.note span {
		border-bottom: 1px dotted var(--line-strong);
	}

	.rows,
	.moves {
		display: grid;
		gap: var(--space-1);
	}

	.row,
	.move {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		padding: var(--space-2) var(--space-3);
		border-radius: var(--radius-xs);
		background: var(--surface-1);
		font-size: var(--text-xs);
		flex-wrap: wrap;
	}

	/* One tinted left edge per severity. Cheaper to scan than a full-row tint and it keeps the
	   rows readable when a dozen of them stack. */
	.row,
	.move {
		border-left: 2px solid var(--line);
	}

	.row.danger,
	.move.danger {
		border-left-color: var(--danger);
	}
	.row.info,
	.move.info {
		border-left-color: var(--info);
	}
	.row.tech,
	.move.tech {
		border-left-color: var(--tech);
	}
	.row.ok,
	.move.ok {
		border-left-color: var(--ok);
	}
	.row.warn,
	.move.warn {
		border-left-color: var(--accent);
	}

	.badge {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		min-width: 5.5rem;
		font-size: var(--text-2xs);
		letter-spacing: var(--track-wide);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.row.danger .badge,
	.move.danger .badge {
		color: var(--danger);
	}
	.row.info .badge,
	.move.info .badge {
		color: var(--info);
	}
	.row.tech .badge,
	.move.tech .badge {
		color: var(--tech);
	}
	.row.ok .badge,
	.move.ok .badge {
		color: var(--ok);
	}
	.row.warn .badge,
	.move.warn .badge {
		color: var(--accent);
	}

	.name {
		font-family: var(--font-mono);
		margin-right: auto;
		color: var(--text-1);
	}

	.versions {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		font-family: var(--font-mono);
	}

	.was {
		color: var(--text-3);
	}

	.is {
		color: var(--text-1);
	}

	.versions :global(.icon) {
		color: var(--text-3);
	}

	.more {
		margin-top: var(--space-3);
		font-size: var(--text-xs);
		color: var(--accent);
		transition: color var(--dur-quick) var(--ease-standard);
	}

	.more:hover {
		color: var(--accent-strong);
	}

	.timeline {
		display: grid;
		gap: var(--space-5);
		margin: 0;
		padding: 0 0 0 var(--space-4);
		list-style: none;
		border-left: 1px solid var(--line);
	}

	.timeline li {
		display: grid;
		gap: var(--space-2);
		position: relative;
	}

	/* The node on the rail. Drawn rather than bulleted so it lines up with the border. */
	.timeline li::before {
		content: '';
		position: absolute;
		left: calc(-1 * var(--space-4) - 4px);
		top: 6px;
		width: 7px;
		height: 7px;
		border-radius: var(--radius-pill);
		background: var(--accent);
		box-shadow: 0 0 0 3px var(--ground);
	}

	.when {
		display: flex;
		align-items: baseline;
		gap: var(--space-3);
		font-size: var(--text-xs);
		color: var(--text-2);
	}

	.count {
		color: var(--text-3);
	}

	.muted {
		color: var(--text-3);
		font-size: var(--text-sm);
	}

	.problem {
		color: var(--danger);
		font-size: var(--text-sm);
	}
</style>
