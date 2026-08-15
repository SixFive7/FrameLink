<script lang="ts">
	/**
	 * Where the fleet disagrees.
	 *
	 * The operator's stated purpose for all of this is seeing drift, so the screen is a
	 * *comparison* and not a dump. A frame carries ~930 packages; rendering them would be a
	 * table nobody reads, and §7.4 holds this console to the same bar as the frame's own
	 * screen. What is on the page instead:
	 *
	 *  1. **One line that answers the whole question** — how many packages every frame agrees
	 *     on, and how many they do not. On a healthy fleet the answer is "929, and none", which
	 *     is the most informative possible screen and takes a second to read.
	 *  2. **A row per frame** with the five numbers, so "which one took its updates" is visible
	 *     without opening anything.
	 *  3. **A row per disagreement**, with the frames grouped under the version they are on.
	 *     Widest disagreement first, because that is the triage order.
	 *
	 * The server does the comparing. This screen never receives a package set.
	 */
	import { onMount } from 'svelte';
	import { api, ApiError } from '$lib/api/client';
	import type { FleetPackagesResponse } from '$lib/api/types';
	import { fleet } from '$lib/stores/fleet.svelte';
	import { describeStatus, faultCount } from '$lib/packages';
	import { plural, timeAgo, timeExact } from '$lib/format';
	import { rise, settle } from '$lib/design/motion';
	import Button from '$lib/components/Button.svelte';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';
	import Fingerprint from '$lib/components/Fingerprint.svelte';
	import Icon from '$lib/components/Icon.svelte';

	let view = $state<FleetPackagesResponse | undefined>();
	let problem = $state<string | undefined>();
	let loading = $state(true);
	let refreshing = $state(false);

	/** Which disagreement rows are expanded. Collapsed by default: the count is the headline. */
	let opened = $state<Record<string, boolean>>({});

	const reporting = $derived(view?.devices ?? []);
	const faulty = $derived(reporting.filter((device) => faultCount(device) > 0).length);
	const identical = $derived(view !== undefined && reporting.length > 1 && view.distinctSets === 1);

	async function load() {
		refreshing = true;
		try {
			view = await api.fleetPackages();
			problem = undefined;
		} catch (cause) {
			problem =
				cause instanceof ApiError ? cause.message : 'The package comparison could not be read.';
		} finally {
			loading = false;
			refreshing = false;
		}
	}

	// Loaded once, and refreshed by hand. Deliberately not polled: a frame reports its packages
	// only when apt has actually moved one, which on a converged fleet is a handful of times a
	// month, and comparing ~930 packages across every device every few seconds to watch a number
	// that changes monthly is work nobody asked for. The fleet list polls because presence is the
	// socket and changes by the second; this does not, because it is not that kind of fact.
	onMount(load);

	function nameOf(deviceId: string): string {
		return (
			view?.devices.find((device) => device.deviceId === deviceId)?.name ??
			fleet.find(deviceId)?.name ??
			deviceId
		);
	}
</script>

<div class="page">
	<header in:rise={{ y: 12 }}>
		<div>
			<h1>Packages</h1>
			<p class="lede">
				Every frame reports everything it has installed, whenever that changes. This is where
				they differ — from each other, and from the {view?.baselineCount ?? 0} packages that were
				reviewed.
			</p>
		</div>
		<div class="tools">
			{#if view}
				<Chip tone="tech" size="sm" title={timeExact(view.baselineReviewedUtc)}>
					Baseline reviewed {timeAgo(view.baselineReviewedUtc)}
				</Chip>
			{/if}
			<Button variant="quiet" icon="refresh" busy={refreshing} onclick={() => void load()}>
				Refresh
			</Button>
		</div>
	</header>

	{#if problem}
		<Card><p class="problem">{problem}</p></Card>
	{:else if loading}
		<Card><p class="muted">Comparing…</p></Card>
	{:else if reporting.length === 0}
		<Card>
			<EmptyState icon="box" title="No frame has reported its packages yet">
				A frame sends its whole package list the first time it comes up, and again whenever
				anything changes. Adopt a frame and this fills in on its own.
			</EmptyState>
		</Card>
	{:else}
		<section class="verdict" in:settle={{ index: 0, count: 3 }}>
			<Card tone={view && view.disagreementTotal > 0 ? 'accent' : 'plain'}>
				<div class="headline">
					<span class="mark" class:agreed={identical} aria-hidden="true">
						<Icon name={identical ? 'check' : 'box'} size={22} />
					</span>
					<div>
						{#if reporting.length === 1}
							<h2>One frame reporting</h2>
							<p>
								{plural(view?.devices[0].installed ?? 0, 'package')} installed. There is nothing
								to compare it with until a second frame reports.
							</p>
						{:else if identical}
							<h2>Every frame is identical</h2>
							<p>
								All {reporting.length} frames carry the same {plural(view?.agreed ?? 0, 'package')} at
								the same versions. One stored set covers the fleet.
							</p>
						{:else}
							<h2>{plural(view?.disagreementTotal ?? 0, 'package')} differ across the fleet</h2>
							<p>
								{reporting.length} frames agree on {view?.agreed ?? 0}, and hold {view?.distinctSets ??
									0} distinct package sets between them.
							</p>
						{/if}
					</div>
				</div>

				{#if faulty > 0}
					<div class="fault">
						<Icon name="alert" size={16} />
						<span>
							{plural(faulty, 'frame')}
							{faulty === 1 ? 'is' : 'are'} missing a reviewed package or running one older than
							the reviewed version. Being <b>newer</b> is not counted here — that is a security
							update doing its job.
						</span>
					</div>
				{/if}
			</Card>
		</section>

		<section in:settle={{ index: 1, count: 3 }}>
			<Card>
				<h2>Frames</h2>
				<div class="frames">
					{#each reporting as device (device.deviceId)}
						<a class="frame" href="/devices/{encodeURIComponent(device.deviceId)}">
							<div class="who">
								<span class="label">{device.name ?? 'Unnamed frame'}</span>
								<Fingerprint deviceId={device.deviceId} size="sm" copyable={false} />
							</div>
							<div class="numbers">
								<span class="stat" title="Packages installed">
									<b>{device.installed}</b><small>installed</small>
								</span>
								<span class="stat info" class:zero={device.ahead === 0} title="Newer than the reviewed version">
									<b>{device.ahead}</b><small>newer</small>
								</span>
								<span class="stat danger" class:zero={device.behind === 0} title="Older than the reviewed version">
									<b>{device.behind}</b><small>older</small>
								</span>
								<span class="stat danger" class:zero={device.missing === 0} title="In the reviewed set and not on this frame">
									<b>{device.missing}</b><small>missing</small>
								</span>
								<span class="stat tech" class:zero={device.extra === 0} title="On this frame and not in the reviewed set">
									<b>{device.extra}</b><small>extra</small>
								</span>
							</div>
							<span class="when" title={timeExact(device.observedUtc)}>
								{timeAgo(device.observedUtc)}
							</span>
							<Icon name="chevronRight" size={15} />
						</a>
					{/each}
				</div>
			</Card>
		</section>

		{#if view && view.disagreementTotal > 0}
			<section in:settle={{ index: 2, count: 3 }}>
				<Card>
					<div class="head">
						<h2>Where they disagree</h2>
						{#if view.disagreements.length < view.disagreementTotal}
							<Chip tone="warn" size="sm">
								showing {view.disagreements.length} of {view.disagreementTotal}
							</Chip>
						{/if}
					</div>

					<div class="rows">
						{#each view.disagreements as row (row.package)}
							{@const open = opened[row.package] ?? false}
							<div class="row" class:open>
								<button
									class="row-head"
									onclick={() => (opened = { ...opened, [row.package]: !open })}
									aria-expanded={open}
								>
									<Icon name="chevronRight" size={14} />
									<code>{row.package}</code>
									<span class="spread">{plural(row.versions.length, 'version')}</span>
									{#if row.baseline}
										<span class="baseline" title="The reviewed version">{row.baseline}</span>
									{:else}
										<Chip tone="tech" size="sm">not in the baseline</Chip>
									{/if}
								</button>

								{#if open}
									<ul class="groups">
										{#each row.versions as group (group.version ?? 'absent')}
											<li>
												{#if group.version}
													<code
														class="version"
														class:reviewed={group.version === row.baseline}
														title={group.version === row.baseline
															? 'The reviewed version'
															: undefined}>{group.version}</code
													>
												{:else}
													<span class="absent">not installed</span>
												{/if}
												<span class="on">
													{#each group.deviceIds as deviceId (deviceId)}
														<a href="/devices/{encodeURIComponent(deviceId)}">{nameOf(deviceId)}</a>
													{/each}
												</span>
											</li>
										{/each}
									</ul>
								{/if}
							</div>
						{/each}
					</div>

					<p class="footnote">
						<Icon name="info" size={14} />
						{describeStatus('ahead').meaning}
					</p>
				</Card>
			</section>
		{/if}
	{/if}
</div>

<style>
	.page {
		display: grid;
		gap: var(--space-6);
	}

	header {
		display: flex;
		align-items: flex-start;
		justify-content: space-between;
		gap: var(--space-6);
		flex-wrap: wrap;
	}

	h1 {
		font-size: var(--text-2xl);
		letter-spacing: var(--track-tight);
	}

	.tools {
		display: flex;
		align-items: center;
		gap: var(--space-3);
	}

	.lede {
		max-width: var(--width-prose);
		margin-top: var(--space-2);
		color: var(--text-2);
		font-size: var(--text-sm);
		line-height: var(--leading-normal);
	}

	h2 {
		font-size: var(--text-lg);
	}

	.headline {
		display: flex;
		align-items: center;
		gap: var(--space-4);
	}

	.headline p {
		margin-top: var(--space-1);
		color: var(--text-2);
		font-size: var(--text-sm);
		line-height: var(--leading-snug);
	}

	/* The verdict mark. Warm by default because a disagreement wants attention; green only
	   once the fleet is genuinely one thing. */
	.mark {
		display: grid;
		place-items: center;
		flex: none;
		width: 44px;
		height: 44px;
		border-radius: var(--radius-pill);
		color: var(--accent);
		background: var(--accent-soft);
		border: 1px solid var(--accent-line);
	}

	.mark.agreed {
		color: var(--ok);
		background: var(--ok-soft);
		border-color: var(--ok-line);
	}

	.fault {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		margin-top: var(--space-4);
		padding: var(--space-3) var(--space-4);
		border-radius: var(--radius-md);
		border: 1px solid var(--danger-line);
		background: var(--danger-soft);
		color: var(--text-2);
		font-size: var(--text-sm);
		line-height: var(--leading-snug);
	}

	.fault :global(.icon) {
		color: var(--danger);
		margin-top: 2px;
	}

	.frames {
		display: grid;
		gap: var(--space-1);
		margin-top: var(--space-3);
	}

	.frame {
		display: flex;
		align-items: center;
		gap: var(--space-4);
		padding: var(--space-3) var(--space-4);
		border-radius: var(--radius-sm);
		text-decoration: none;
		color: var(--text-1);
		transition:
			background-color var(--dur-quick) var(--ease-standard),
			transform var(--dur-quick) var(--ease-standard);
	}

	.frame:hover {
		background: var(--surface-2);
		transform: translateX(2px);
	}

	.who {
		display: grid;
		gap: 2px;
		min-width: 0;
		flex: 1 1 14rem;
	}

	.label {
		font-weight: var(--weight-medium);
		white-space: nowrap;
		overflow: hidden;
		text-overflow: ellipsis;
	}

	.numbers {
		display: flex;
		align-items: baseline;
		gap: var(--space-4);
	}

	.stat {
		display: grid;
		justify-items: center;
		gap: 1px;
		min-width: 3.2rem;
	}

	.stat b {
		font-family: var(--font-mono);
		font-size: var(--text-md);
		font-weight: var(--weight-semibold);
		font-variant-numeric: tabular-nums;
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

	/* A zero is not news. Draining the colour out of it is what makes a non-zero one carry. */
	.stat.zero b {
		color: var(--text-3);
		font-weight: var(--weight-normal);
	}

	.when {
		font-size: var(--text-xs);
		color: var(--text-3);
		white-space: nowrap;
	}

	.frame :global(.icon) {
		color: var(--text-3);
		flex: none;
	}

	.head {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		margin-bottom: var(--space-3);
	}

	.head h2 {
		margin-right: auto;
	}

	.rows {
		display: grid;
		gap: var(--space-1);
	}

	.row-head {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		width: 100%;
		padding: var(--space-3) var(--space-3);
		border-radius: var(--radius-sm);
		text-align: left;
		color: var(--text-1);
		transition: background-color var(--dur-quick) var(--ease-standard);
	}

	.row-head:hover {
		background: var(--surface-2);
	}

	.row-head :global(.icon) {
		color: var(--text-3);
		flex: none;
		transition: transform var(--dur-quick) var(--ease-standard);
	}

	.row.open .row-head :global(.icon) {
		transform: rotate(90deg);
	}

	.row-head code {
		font-family: var(--font-mono);
		font-size: var(--text-sm);
		margin-right: auto;
	}

	.spread {
		font-size: var(--text-xs);
		color: var(--accent);
		white-space: nowrap;
	}

	.baseline {
		font-family: var(--font-mono);
		font-size: var(--text-xs);
		color: var(--text-3);
		white-space: nowrap;
	}

	.groups {
		display: grid;
		gap: var(--space-2);
		margin: 0 0 var(--space-3) var(--space-8);
		padding: 0 0 0 var(--space-4);
		list-style: none;
		border-left: 1px solid var(--line);
		animation: unfold var(--dur-base) var(--ease-entrance);
	}

	@keyframes unfold {
		from {
			opacity: 0;
			transform: translateY(-4px);
		}
	}

	.groups li {
		display: flex;
		align-items: baseline;
		gap: var(--space-4);
		flex-wrap: wrap;
	}

	.version {
		font-family: var(--font-mono);
		font-size: var(--text-xs);
		padding: 2px var(--space-2);
		border-radius: var(--radius-xs);
		background: var(--surface-2);
		min-width: 12rem;
	}

	/* The reviewed version, marked so an operator can see which group is the known one. */
	.version.reviewed {
		background: var(--ok-soft);
		color: var(--ok);
	}

	.absent {
		font-size: var(--text-xs);
		color: var(--danger);
		min-width: 12rem;
	}

	.on {
		display: flex;
		gap: var(--space-3);
		flex-wrap: wrap;
		font-size: var(--text-sm);
	}

	.on a {
		color: var(--text-2);
		text-decoration: none;
		border-bottom: 1px dotted var(--line-strong);
	}

	.on a:hover {
		color: var(--accent);
	}

	.footnote {
		display: flex;
		align-items: flex-start;
		gap: var(--space-2);
		margin-top: var(--space-5);
		color: var(--text-3);
		font-size: var(--text-xs);
		line-height: var(--leading-normal);
	}

	.footnote :global(.icon) {
		flex: none;
		margin-top: 2px;
	}

	.muted {
		color: var(--text-3);
		font-size: var(--text-sm);
	}

	.problem {
		color: var(--danger);
		font-size: var(--text-sm);
	}

	@media (max-width: 60rem) {
		.frame {
			flex-wrap: wrap;
		}
		.numbers {
			gap: var(--space-3);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.frame:hover {
			transform: none;
		}
		.groups {
			animation: none;
		}
	}
</style>
