<script lang="ts">
	/**
	 * Live reconciliation — §3.5's first-class screen.
	 *
	 * The spec names its contents in one sentence: "current resource and phase, settings applied,
	 * settings still drifted, reboots expected before convergence, and the per-resource status
	 * list". What that sentence does not say, and what makes the difference between a table and a
	 * diagnosis, is the *order*.
	 *
	 * **The fault is the headline.** When a resource has exhausted its budget and given up, that
	 * is the whole story of the frame, and putting it in row 41 of a list of 78 buries it under
	 * seventy-seven rows of nothing-happened. It goes at the top, at full width, with its
	 * expected-versus-observed delta and its attempt count, because those are the two facts an
	 * operator acts on.
	 *
	 * **Blocked resources hang off what blocks them.** On the first full provision of a real frame
	 * the report read 37 in sync, 1 escalated, 12 blocked, 32 reboots expected — and every one of
	 * the twelve was downstream of the one escalation. §2.2 built the DAG so a dependent would be
	 * marked `Blocked(dependency)` "rather than letting them fail confusingly on their own", and
	 * rendering that as a flat list throws away the entire point: twelve mysteries instead of one
	 * cause with a visible blast radius.
	 *
	 * **Waiting is not failing.** A blocked resource was never attempted — no act, no reboot, no
	 * attempt spent. A resource blocked on `the Fleet Manager` was not even observed: that is the
	 * wire spelling of `Unevaluable`, and it means the frame *could not ask*. Neither is red here.
	 * The distinction cost real work to build on the agent side and it would be undone by a
	 * stylesheet that treated "waiting" and "broken" as the same colour.
	 *
	 * **The denominator is resources, not time.** There is no bar that fills smoothly. A full
	 * provision is about half an hour of reboots at 40–60 s each, and a percentage that implied
	 * otherwise would be a lie the first time a resource backed off. What is drawn instead is a
	 * census of things the frame actually counted, and the time estimate is stated as a range
	 * derived from the reboot count, which is the only honest answer to "how long".
	 *
	 * Liveness comes from the `/api/events` stream with a 20 s poll underneath — see
	 * `$lib/stores/reconcile.svelte` for why that is the right mechanism and not a compromise.
	 */
	import { page } from '$app/state';
	import { api, ApiError } from '$lib/api/client';
	import { fleet } from '$lib/stores/fleet.svelte';
	import { reconcile } from '$lib/stores/reconcile.svelte';
	import {
		blockedBehind,
		census,
		countBlocked,
		describeEvent,
		describeLoop,
		describePhase,
		describeResource,
		faults,
		orphanedBlocks,
		rebootTime,
		SILENT_AUTHORITY
	} from '$lib/reconcile';
	import { plural, timeAgo, timeExact } from '$lib/format';
	import { collapse, rise, settle } from '$lib/design/motion';
	import BlockedTree from '$lib/components/BlockedTree.svelte';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import ConfirmDialog from '$lib/components/ConfirmDialog.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';
	import Icon from '$lib/components/Icon.svelte';
	import ResourceRow from '$lib/components/ResourceRow.svelte';

	const deviceId = $derived(decodeURIComponent(page.params.id ?? ''));
	const device = $derived(fleet.find(deviceId));

	const report = $derived(reconcile.latest);
	const resources = $derived(reconcile.resources);
	const loop = $derived(report ? describeLoop(report.loopState) : undefined);
	const phase = $derived(describePhase(report?.currentPhase));
	const numbers = $derived(report ? census(report) : undefined);

	/** Everything that has given up, worst first. Usually none; occasionally one; rarely more. */
	const faulted = $derived(faults(resources));

	/** Each fault with its blast radius already resolved, so the markup stays about layout. */
	const blastRadius = $derived(
		faulted.map((resource) => {
			const waiting = blockedBehind(resources, resource.name);
			return { resource, waiting, total: countBlocked(waiting) };
		})
	);

	/** Names already drawn inside a fault's tree, so nothing is rendered twice. */
	const claimed = $derived.by(() => {
		const names = new Set<string>();
		const walk = (nodes: ReturnType<typeof blockedBehind>) => {
			for (const node of nodes) {
				names.add(node.resource.name);
				walk(node.waiting);
			}
		};
		for (const entry of blastRadius) walk(entry.waiting);
		return names;
	});

	/** Blocked resources whose blocker is not a fault on this screen — grouped by what they wait on. */
	const waitingElsewhere = $derived(orphanedBlocks(resources, claimed));

	const remainder = $derived(
		numbers ? Math.max(0, numbers.total - numbers.inSync - numbers.drifted - numbers.blocked) : 0
	);

	/** The census bar. Every segment is a number the frame sent; the tail is what it did not walk. */
	const segments = $derived(
		numbers
			? [
					{ key: 'in-sync', count: numbers.inSync, tone: 'ok', label: 'verified' },
					{ key: 'drifted', count: numbers.drifted, tone: 'warn', label: 'drifted' },
					{ key: 'blocked', count: numbers.blocked, tone: 'muted', label: 'waiting' },
					{ key: 'unwalked', count: remainder, tone: 'empty', label: 'not walked this pass' }
				].filter((segment) => segment.count > 0)
			: []
	);

	const remaining = $derived(numbers ? rebootTime(numbers.rebootsExpected) : undefined);

	/** How many rows the catalog list draws before the rest are folded away. */
	const FIRST_ROWS = 14;
	let expanded = $state(false);
	const listed = $derived(expanded ? resources : resources.slice(0, FIRST_ROWS));

	/**
	 * §2.5 rung 3's **retry**, which is the one action this screen exists to offer and had no
	 * button until now — the ladder stopped the resource, told the operator, and left them
	 * reading a delta with nothing to press.
	 *
	 * Per resource, because that is what this card is about. The frame-wide form exists on the
	 * API for a device stopped under rung 4 with several resources given up at once; here the
	 * operator is looking at one fault with its blast radius drawn underneath it, so the button
	 * asks about that one and the dependents follow on their own — a blocked resource has spent
	 * no attempt and has no budget to reset.
	 *
	 * Keyed by resource name rather than a single flag, so two faults on one frame get two
	 * independent buttons instead of one that greys them both out.
	 */
	let pressed = $state<Record<string, string>>({});
	let pressing = $state<string | undefined>(undefined);

	/**
	 * The frame-wide restart — the remote half of the second button on the frame's own screen.
	 *
	 * It appears only once the frame has actually stopped, for the same reason the button on the
	 * frame does: a restart offered against a frame that is working is a minute of somebody's
	 * photographs taken away for nothing.
	 *
	 * It is *disabled* rather than hidden while the frame is offline, and the disabled button says
	 * why. A button that silently does nothing is worse than one that is visibly unavailable — and
	 * the condition is not a guess: §3.5 makes presence the socket, so `reconcile.online` is the
	 * same fact the server would answer 409 on.
	 */
	let restartSaid = $state<string | undefined>(undefined);
	let restarting = $state(false);

	const stopped = $derived(faulted.length > 0);

	async function restart() {
		if (restarting) return;
		restarting = true;
		try {
			const answer = await api.restart(deviceId);
			restartSaid = answer.detail;
		} catch (error) {
			restartSaid =
				error instanceof ApiError ? error.message : 'That did not reach the Fleet Manager.';
		} finally {
			restarting = false;
		}
	}

	/**
	 * The off switch — the one action on this screen that no action on this screen can undo.
	 *
	 * Unlike the restart it is *not* gated on the frame having stopped. A restart offered against a
	 * working frame is a minute of somebody's photographs taken away for nothing, so that one waits
	 * for a fault; an off switch that only worked on broken frames would be no off switch at all.
	 *
	 * It is disabled rather than hidden while the frame is offline, and the disabled state says the
	 * thing the server cannot resolve either: a frame with no socket is already off or has lost its
	 * network, and from here those are the same picture. A button that silently did nothing here
	 * would leave an operator believing a frame in somebody's living room had been switched off.
	 *
	 * The acknowledgement is mandatory rather than a second click, for the same reason the
	 * unattended firmware write's is: accepting the consequence *is* the decision, and a dialog
	 * dismissable with one click makes the most consequential action on the page the cheapest one.
	 */
	let shutdownSaid = $state<string | undefined>(undefined);
	let shuttingDown = $state(false);
	let confirmShutdown = $state(false);
	let shutdownAccepted = $state(false);

	async function shutdown() {
		if (shuttingDown) return;
		shuttingDown = true;
		try {
			const answer = await api.shutdown(deviceId);
			shutdownSaid = answer.detail;
		} catch (error) {
			// The server's own sentence, including the 409 that means the frame was never reached and
			// is therefore still on. That is a real answer rather than a failure to hide: the button is
			// disabled while the frame is offline, so reaching this means the socket died between the
			// render and the click, and the operator has to be told which of the two happened.
			shutdownSaid =
				error instanceof ApiError ? error.message : 'That did not reach the Fleet Manager.';
		} finally {
			shuttingDown = false;
			confirmShutdown = false;
			shutdownAccepted = false;
		}
	}

	async function retry(resource: string) {
		if (pressing) return;
		pressing = resource;
		try {
			const answer = await api.retry(deviceId, resource);
			pressed = { ...pressed, [resource]: answer.detail };
		} catch (error) {
			// The server's own sentence, including the 409 that means the frame is not connected.
			// That is a real answer rather than a failure to hide: nothing replays a retry on
			// reconnect, so the operator has to know to press it again.
			pressed = {
				...pressed,
				[resource]: error instanceof ApiError ? error.message : 'That did not reach the Fleet Manager.'
			};
		} finally {
			pressing = undefined;
		}
	}

	// One effect rather than `onMount`, because a navigation from one frame's reconcile screen to
	// another's reuses this component: the watcher has to be re-pointed, not left running on the
	// frame the operator has stopped looking at. The cleanup covers both that and unmount.
	$effect(() => {
		if (!deviceId) return;
		reconcile.watch(deviceId);
		return () => reconcile.stop();
	});

	// A fresh report is the frame answering, so the acknowledgement stops being the newest thing
	// on screen. Clearing it here rather than on a timer means the sentence lives exactly as long
	// as it is the most recent news about this frame.
	$effect(() => {
		void reconcile.latest?.sequence;
		if (Object.keys(pressed).length > 0 && !pressing) pressed = {};
	});
</script>

<!-- Restart and shut down, defined once and rendered in one of two places. On a frame that has
     stopped it belongs at the top, because the fault is the headline and these are what an operator
     does about it. On a frame with nothing wrong the restart half is not drawn at all and the off
     switch goes to the foot: it is still always available, but a healthy frame's reconcile screen
     should not open on its own off switch. -->
{#snippet powerCard()}
	<section class="restart" in:rise={{ y: 8 }}>
		<Card tone={stopped ? 'danger' : 'plain'}>
			{#if stopped}
				<h3>This frame has stopped and is waiting for a person</h3>
				<p class="note">
					It is showing the same two buttons on its own screen, and both of them are here too.
					Restarting gives every setting that gave up its attempts back and restarts the frame, so
					it starts again from the top.
				</p>
				<div class="fault-action">
					<button class="retry" disabled={restarting || !reconcile.online} onclick={restart}>
						<Icon name="refresh" size={15} />
						{restarting ? 'Asking…' : 'Restart and try again'}
					</button>
					{#if !reconcile.online}
						<p class="retry-note">
							Unavailable while this frame is offline. Nothing queues a restart, so it has to be
							asked while the frame is connected — or pressed on the frame itself.
						</p>
					{:else if restartSaid}
						<p class="retry-said" in:collapse>{restartSaid}</p>
					{:else}
						<p class="retry-note">
							The frame goes down as soon as it is asked and comes back in about a minute.
						</p>
					{/if}
				</div>
			{/if}

			<h3 class:second={stopped}>Switch this frame off</h3>
			<p class="note">
				This is the only thing on this page that nothing on this page can undo. A frame that is off
				holds no connection, so it disappears from this Fleet Manager and no remote action reaches
				it — not this button, not a restart, not an update. Somebody has to be in the room with it
				and unplug it and plug it in again.
			</p>
			<div class="fault-action">
				<button
					class="retry danger"
					disabled={shuttingDown || !reconcile.online}
					onclick={() => (confirmShutdown = true)}
				>
					<Icon name="power" size={15} />
					{shuttingDown ? 'Asking…' : 'Shut down'}
				</button>
				{#if !reconcile.online}
					<p class="retry-note">
						This frame is not connected, so it cannot be asked. It is either already off or it has
						lost its network, and nothing here can tell which — somebody at the frame is the only
						way to find out.
					</p>
				{:else if shutdownSaid}
					<p class="retry-said" in:collapse>{shutdownSaid}</p>
				{:else}
					<p class="retry-note">
						Offered whether or not anything is wrong. It goes down as soon as it is asked, unless
						its microphone unit is having firmware written to it — a frame refuses both power
						actions during a write, because losing mains in the middle of one destroys that unit.
					</p>
				{/if}
			</div>
		</Card>
	</section>
{/snippet}

<div class="page">
	<a class="back" href="/devices/{encodeURIComponent(deviceId)}">
		<Icon name="arrowLeft" size={15} />
		{device?.name ?? 'This frame'}
	</a>

	<header in:rise={{ y: 12 }}>
		<div class="title">
			<h1>Reconciliation</h1>
			<div class="chips">
				{#if loop}
					<Chip tone={loop.tone} icon={loop.icon} size="md" title={loop.meaning}>{loop.label}</Chip>
				{/if}
				{#if !reconcile.online}
					<Chip tone="muted" icon="clock" size="sm">
						Offline — this is the last thing it said
					</Chip>
				{/if}
				{#if reconcile.refreshedAt}
					<Chip
						tone={reconcile.live ? 'info' : 'muted'}
						size="sm"
						dot
						pulse={reconcile.live}
						title={reconcile.live
							? 'Streaming: the server pushes a nudge the instant a report lands.'
							: 'The event stream is not open, so this is refreshing on a 20-second poll.'}
					>
						{reconcile.live ? 'live' : 'polling'}
					</Chip>
				{/if}
			</div>
		</div>

		{#if report}
			<p class="stamp" title={timeExact(report.generatedUtc)}>
				Frame reported {timeAgo(report.generatedUtc)}
				<span class="seq">#{report.sequence}</span>
			</p>
		{/if}
	</header>

	{#if stopped}{@render powerCard()}{/if}

	{#if reconcile.problem}
		<div class="banner" role="status" in:rise={{ y: 8 }}>
			<Icon name="alert" size={16} />
			<div>
				<b>{reconcile.problem}</b>
				{#if report}
					Showing the report from {timeAgo(report.generatedUtc)}.
				{/if}
			</div>
		</div>
	{/if}

	{#if reconcile.loading && !report}
		<Card>
			<p class="muted">Reading this frame’s report…</p>
		</Card>
	{:else if reconcile.neverReported}
		<Card padding="none">
			<EmptyState icon="frame" title="This frame has never sent a report">
				A frame reports its whole reconciliation state as one message, on every pass. Nothing
				has arrived yet — which is what a pending frame, or one adopted a moment ago, looks
				like. It is not an error, and there is nothing to fix.
			</EmptyState>
		</Card>
	{:else if report && numbers}
		{#each blastRadius as entry, index (entry.resource.name)}
			{@const look = describeResource(entry.resource)}
			<section in:settle={{ index, count: blastRadius.length, y: 14 }}>
				<Card tone="danger">
					<div class="fault-head">
						<span class="fault-badge">
							<Icon name={look.icon} size={15} />
							{look.label}
						</span>
						<code class="fault-name">{entry.resource.name}</code>
					</div>

					<p class="fault-meaning">{look.meaning}</p>

					{#if entry.resource.delta}
						<p class="fault-delta">{entry.resource.delta}</p>
					{/if}

					<div class="fault-facts">
						{#if entry.resource.attemptBudget > 0}
							<div class="fact">
								<b>{entry.resource.attempts} of {entry.resource.attemptBudget}</b>
								<small>attempts spent</small>
							</div>
						{:else if entry.resource.attempts > 0}
							<div class="fact">
								<b>{entry.resource.attempts}</b>
								<small>attempts spent</small>
							</div>
						{/if}
						{#if entry.resource.escalations > 0}
							<div class="fact">
								<b>{entry.resource.escalations}</b>
								<small>times you were told</small>
							</div>
						{/if}
						{#if entry.total > 0}
							<div class="fact">
								<b>{entry.total}</b>
								<small>waiting behind it</small>
							</div>
						{/if}
						{#if entry.resource.nextAttemptUtc}
							<div class="fact">
								<b title={timeExact(entry.resource.nextAttemptUtc)}>
									{timeAgo(entry.resource.nextAttemptUtc)}
								</b>
								<small>next attempt</small>
							</div>
						{/if}
					</div>

					<div class="fault-action">
						<button
							class="retry"
							disabled={pressing === entry.resource.name}
							onclick={() => retry(entry.resource.name)}
						>
							<Icon name="refresh" size={15} />
							{pressing === entry.resource.name ? 'Asking…' : 'Try this again'}
						</button>
						{#if pressed[entry.resource.name]}
							<p class="retry-said" in:collapse>{pressed[entry.resource.name]}</p>
						{:else}
							<p class="retry-note">
								Gives this setting its attempts back. Nothing else changes, and the frame does
								not reboot until it decides to.
							</p>
						{/if}
					</div>

					{#if entry.total > 0}
						<h3>Standing still because of it</h3>
						<p class="note">
							{plural(entry.total, 'resource')}
							{entry.total === 1 ? 'was' : 'were'} never attempted — each one depends on this,
							directly or through another. Nothing here is broken; fixing the resource above
							releases all of them.
						</p>
						<BlockedTree nodes={entry.waiting} currentResource={report.currentResource} />
					{/if}
				</Card>
			</section>
		{/each}

		<section in:settle={{ index: blastRadius.length, count: blastRadius.length + 1, y: 14 }}>
			<Card>
				<div class="head">
					<h2>Progress</h2>
					{#if report.currentResource}
						<Chip tone="info" icon="refresh" size="sm">
							{report.currentResource}{phase ? ` — ${phase}` : ''}
						</Chip>
					{:else if loop}
						<Chip tone={loop.tone} size="sm">{loop.meaning}</Chip>
					{/if}
				</div>

				<div class="bar" role="img" aria-label="{numbers.inSync} of {numbers.total} resources verified">
					{#each segments as segment (segment.key)}
						<span
							class="segment {segment.tone}"
							style:flex-grow={segment.count}
							title="{segment.count} {segment.label}"
						></span>
					{/each}
				</div>

				<div class="stats">
					<div class="stat ok">
						<b>{numbers.inSync}</b>
						<small>verified of {numbers.total}</small>
					</div>
					<div class="stat warn" class:zero={numbers.drifted === 0}>
						<b>{numbers.drifted}</b>
						<small>still drifted</small>
					</div>
					<div class="stat" class:zero={numbers.blocked === 0}>
						<b>{numbers.blocked}</b>
						<small>waiting on something</small>
					</div>
					<div class="stat accent" class:zero={numbers.rebootsExpected === 0}>
						<b>{numbers.rebootsExpected}</b>
						<small>reboots expected</small>
					</div>
				</div>

				<p class="note">
					{#if numbers.rebootsExpected === 0}
						Every resource is verified. §2.4 never claims "applied" from a successful write —
						each of these had to survive a boot to count.
					{:else}
						Every resource reboots, with no exceptions (§2.4), so the reboots left to go
						<i>are</i> the resources left to verify. At 40–60 seconds a cycle that is
						<b>{remaining}</b>. This is a count of resources, not a percentage of time: the
						frame cannot know how long a backoff will last, so nothing here pretends to.
					{/if}
				</p>

				{#if !numbers.complete}
					<p class="note quiet">
						<Icon name="info" size={13} />
						This report carries {plural(report.resources.length, 'resource')} of {numbers.total} —
						the agent sends the resources it has walked so far during a pass, and exactly one
						just before a reboot. The rows below are the newest status seen for each, which is
						why the list stays whole while the report does not.
					</p>
				{/if}
			</Card>
		</section>

		{#if waitingElsewhere.length > 0}
			<section in:rise={{ y: 12 }}>
				<Card>
					<div class="head">
						<h2>Waiting</h2>
						<Chip tone="muted" size="sm">not a failure</Chip>
					</div>
					<p class="note">
						These were never attempted, so nothing was tried and nothing rebooted. They clear on
						their own the moment what they wait on is in sync.
					</p>

					{#each waitingElsewhere as group (group.blocker)}
						<h3>
							{#if group.blocker === SILENT_AUTHORITY}
								Could not ask — waiting for the Fleet Manager
							{:else}
								Waiting for <code>{group.blocker}</code>
							{/if}
						</h3>
						{#if group.blocker === SILENT_AUTHORITY}
							<p class="note quiet">
								The frame could not reach the authority that owns these values, so it concluded
								nothing rather than guessing. No attempt was spent and nothing rebooted — this
								is a frame behaving correctly while a server was quiet, not a frame with a
								fault.
							</p>
						{/if}
						<div class="rows">
							{#each group.waiting as resource (resource.name)}
								<ResourceRow {resource} current={resource.name === report.currentResource} />
							{/each}
						</div>
					{/each}
				</Card>
			</section>
		{/if}

		<section in:rise={{ y: 12 }}>
			<Card>
				<div class="head">
					<h2>Every resource</h2>
					<Chip tone="tech" size="sm">{resources.length} reported</Chip>
					{#if reconcile.unreported > 0}
						<Chip tone="muted" size="sm" title="In the catalog, but not in any report yet.">
							{reconcile.unreported} not yet seen
						</Chip>
					{/if}
				</div>

				<div class="rows">
					{#each listed as resource (resource.name)}
						<ResourceRow {resource} current={resource.name === report.currentResource} terse />
					{/each}
				</div>

				{#if resources.length > FIRST_ROWS}
					<button class="more" onclick={() => (expanded = !expanded)}>
						{expanded ? 'Show fewer' : `Show all ${resources.length}`}
					</button>
				{/if}
			</Card>
		</section>

		<section in:rise={{ y: 12 }}>
			<Card>
				<div class="head">
					<h2>What happened</h2>
					<Chip tone="muted" size="sm">one month is kept</Chip>
				</div>

				{#if reconcile.events.length === 0}
					<p class="muted">
						Nothing recorded yet. Events are written when something changes — drift found, an
						escalation, a boot, convergence — so an empty history is a quiet frame.
					</p>
				{:else}
					<ol class="timeline">
						{#each reconcile.events as moment, index (`${moment.occurredUtc}-${index}`)}
							{@const look = describeEvent(moment.kind)}
							<li transition:collapse class={look.tone}>
								<div class="when" title={timeExact(moment.occurredUtc)}>
									<span class="kind">
										<Icon name={look.icon} size={12} />
										{look.label}
									</span>
									{timeAgo(moment.occurredUtc)}
									{#if moment.resource}
										<code>{moment.resource}</code>
									{/if}
								</div>
								<p class="summary">{moment.summary}</p>
								{#if moment.delta}
									<p class="delta">{moment.delta}</p>
								{/if}
							</li>
						{/each}
					</ol>
				{/if}
			</Card>
		</section>
	{/if}

	{#if !stopped}{@render powerCard()}{/if}
</div>

<ConfirmDialog
	bind:open={confirmShutdown}
	title="Switch this frame off?"
	confirmLabel="I accept this — switch it off"
	cancelLabel="No, leave it running"
	tone="danger"
	wide
	busy={shuttingDown}
	confirmDisabled={!shutdownAccepted}
	onconfirm={() => void shutdown()}
	oncancel={() => {
		confirmShutdown = false;
		shutdownAccepted = false;
	}}
>
	<!-- The consequence stated as a cost, in the order it lands on somebody: the frame stops, it
	     leaves this Fleet Manager, nothing here brings it back, a person has to go to it. The
	     firmware refusal is fourth rather than a footnote, because it is the one case where pressing
	     the button leaves the frame exactly as it was. -->
	<ul class="warning">
		<li>
			<b>The frame stops.</b> Its photographs, its screen and its calls stop with it, and anyone
			in the household who was about to use it will find it dark.
		</li>
		<li>
			<b>It disappears from this Fleet Manager and stays gone.</b> A frame that is off holds no
			connection, so there is nothing here to press, nothing to watch and nothing to report.
		</li>
		<li>
			<b>No remote action brings it back — not this page, not a restart, not an update.</b>
			Somebody has to be in the room with it, unplug it and plug it in again. If nobody can get to
			it, it stays off.
		</li>
		<li>
			<b>It may refuse, and that is deliberate.</b> A frame writing firmware to its microphone
			unit turns both power actions down until the write finishes, because losing mains in the
			middle of one destroys that unit. If that happens the frame stays on and says what to wait
			for on its own screen.
		</li>
	</ul>

	<p class="scope">
		It applies to <b>{device?.name ?? deviceId}</b> and to no other frame.
	</p>

	<label class="accept">
		<input type="checkbox" bind:checked={shutdownAccepted} />
		<span>
			I understand that this frame will go off, that nothing here can bring it back, and that
			somebody has to be there in person.
		</span>
	</label>
</ConfirmDialog>

<style>
	.page {
		display: grid;
		gap: var(--space-6);
	}

	.back {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		font-size: var(--text-sm);
		color: var(--text-3);
		text-decoration: none;
		justify-self: start;
		transition:
			color var(--dur-quick) var(--ease-standard),
			transform var(--dur-quick) var(--ease-standard);
	}

	.back:hover {
		color: var(--text-1);
		transform: translateX(-2px);
	}

	header {
		display: flex;
		align-items: flex-start;
		justify-content: space-between;
		gap: var(--space-6);
		flex-wrap: wrap;
	}

	.title {
		display: grid;
		gap: var(--space-3);
		min-width: 0;
	}

	h1 {
		font-size: var(--text-2xl);
	}

	.chips {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		flex-wrap: wrap;
	}

	.stamp {
		font-size: var(--text-xs);
		color: var(--text-3);
		font-variant-numeric: tabular-nums;
	}

	.seq {
		font-family: var(--font-mono);
		color: var(--text-3);
		opacity: 0.7;
	}

	.banner {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		padding: var(--space-4) var(--space-5);
		border-radius: var(--radius-md);
		border: 1px solid var(--danger-line);
		background: var(--danger-soft);
		color: var(--text-2);
		font-size: var(--text-sm);
		line-height: var(--leading-snug);
	}

	.banner b {
		color: var(--danger);
	}

	.banner :global(.icon) {
		color: var(--danger);
		flex: none;
		margin-top: 2px;
	}

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

	h3 code {
		font-family: var(--font-mono);
		letter-spacing: var(--track-normal);
		text-transform: none;
		color: var(--text-2);
	}

	/* ── the fault, which is the headline ───────────────────────────────────────────────── */

	.fault-head {
		display: flex;
		align-items: baseline;
		gap: var(--space-3);
		flex-wrap: wrap;
	}

	.fault-badge {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--danger);
	}

	.fault-name {
		font-family: var(--font-mono);
		font-size: var(--text-xl);
		color: var(--text-1);
		overflow-wrap: anywhere;
	}

	.fault-meaning {
		margin-top: var(--space-2);
		font-size: var(--text-sm);
		color: var(--text-2);
		line-height: var(--leading-normal);
		max-width: var(--width-prose);
	}

	/* The delta is the one string an operator acts on, so it is set apart and set in mono. */
	.fault-delta {
		margin-top: var(--space-4);
		padding: var(--space-3) var(--space-4);
		border-radius: var(--radius-sm);
		background: var(--surface-sunken);
		border: 1px solid var(--line);
		font-family: var(--font-mono);
		font-size: var(--text-xs);
		color: var(--text-1);
		line-height: var(--leading-normal);
		overflow-wrap: anywhere;
	}

	.fault-facts {
		display: flex;
		gap: var(--space-8);
		flex-wrap: wrap;
		margin-top: var(--space-5);
	}

	.fact {
		display: grid;
		gap: 2px;
	}

	.fact b {
		font-family: var(--font-mono);
		font-size: var(--text-lg);
		font-weight: var(--weight-semibold);
		font-variant-numeric: tabular-nums;
		line-height: 1;
		color: var(--text-1);
	}

	.fact small {
		font-size: var(--text-2xs);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	/* ── the one action this screen offers (§2.5 rung 3) ────────────────────────────────── */

	.fault-action {
		display: grid;
		gap: var(--space-2);
		justify-items: start;
		margin-top: var(--space-5);
	}

	.restart h3.second {
		margin-top: var(--space-6);
		padding-top: var(--space-5);
		border-top: 1px solid var(--line);
	}

	/* The one destructive button on this screen, and the only one that reads as destructive before
	   it is hovered. Everything else here is a neutral affordance because everything else here is
	   recoverable. */
	.retry.danger {
		border-color: var(--danger-line, var(--line));
		color: var(--danger-ink, var(--text-1));
	}

	.retry.danger:hover:not(:disabled) {
		border-color: var(--danger-ink, var(--accent));
		color: var(--danger-ink, var(--accent));
	}

	.warning {
		margin: 0;
		padding-left: var(--space-5);
		display: grid;
		gap: var(--space-3);
	}

	.warning b {
		color: var(--text-1);
	}

	.scope {
		margin: var(--space-4) 0 0;
	}

	.accept {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		margin-top: var(--space-4);
		cursor: pointer;
	}

	.accept input {
		margin-top: 2px;
		flex: none;
	}

	.restart h3 {
		margin: 0 0 6px;
		font-size: 15px;
	}

	.restart .note {
		margin: 0 0 12px;
	}

	.retry {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		padding: var(--space-2) var(--space-4);
		border: 1px solid var(--line);
		border-radius: var(--radius-sm);
		background: var(--surface-sunken);
		font-size: var(--text-xs);
		font-weight: var(--weight-semibold);
		color: var(--text-1);
		transition:
			border-color var(--dur-quick) var(--ease-standard),
			color var(--dur-quick) var(--ease-standard);
	}

	.retry:hover:not(:disabled) {
		border-color: var(--accent);
		color: var(--accent);
	}

	.retry:disabled {
		color: var(--text-3);
		cursor: default;
	}

	.retry-note,
	.retry-said {
		font-size: var(--text-xs);
		line-height: var(--leading-normal);
		max-width: var(--width-prose);
	}

	.retry-note {
		color: var(--text-3);
	}

	.retry-said {
		color: var(--text-2);
	}

	/* ── the census ─────────────────────────────────────────────────────────────────────── */

	.bar {
		display: flex;
		gap: 2px;
		height: 10px;
		margin-bottom: var(--space-5);
		border-radius: var(--radius-pill);
		overflow: hidden;
		background: var(--surface-sunken);
	}

	.segment {
		flex-basis: 0;
		min-width: 3px;
		transition: flex-grow var(--dur-slow) var(--ease-standard);
	}

	.segment.ok {
		background: var(--ok);
	}
	.segment.warn {
		background: var(--accent);
	}
	.segment.muted {
		background: var(--line-strong);
	}
	.segment.empty {
		background: transparent;
	}

	.stats {
		display: flex;
		gap: var(--space-8);
		flex-wrap: wrap;
		margin-bottom: var(--space-4);
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

	.stat.ok b {
		color: var(--ok);
	}
	.stat.warn b {
		color: var(--accent);
	}
	.stat.accent b {
		color: var(--accent);
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

	.note b {
		color: var(--text-2);
	}

	.note.quiet {
		display: flex;
		align-items: flex-start;
		gap: var(--space-2);
		margin-top: var(--space-3);
	}

	.note.quiet :global(.icon) {
		flex: none;
		margin-top: 2px;
	}

	.rows {
		display: grid;
		gap: var(--space-1);
		margin-top: var(--space-3);
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

	/* ── history ────────────────────────────────────────────────────────────────────────── */

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
		gap: var(--space-1);
		position: relative;
	}

	.timeline li::before {
		content: '';
		position: absolute;
		left: calc(-1 * var(--space-4) - 4px);
		top: 6px;
		width: 7px;
		height: 7px;
		border-radius: var(--radius-pill);
		background: var(--text-3);
		box-shadow: 0 0 0 3px var(--ground);
	}

	.timeline li.danger::before {
		background: var(--danger);
	}
	.timeline li.warn::before {
		background: var(--accent);
	}
	.timeline li.ok::before {
		background: var(--ok);
	}
	.timeline li.info::before {
		background: var(--info);
	}

	.when {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		flex-wrap: wrap;
		font-size: var(--text-xs);
		color: var(--text-3);
	}

	.kind {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		font-size: var(--text-2xs);
		letter-spacing: var(--track-wide);
		text-transform: uppercase;
	}

	.timeline li.danger .kind {
		color: var(--danger);
	}
	.timeline li.warn .kind {
		color: var(--accent);
	}
	.timeline li.ok .kind {
		color: var(--ok);
	}
	.timeline li.info .kind {
		color: var(--info);
	}

	.when code {
		font-family: var(--font-mono);
		color: var(--text-2);
	}

	.summary {
		font-size: var(--text-sm);
		color: var(--text-1);
		line-height: var(--leading-snug);
		max-width: var(--width-prose);
	}

	.delta {
		font-family: var(--font-mono);
		font-size: var(--text-2xs);
		color: var(--text-3);
		overflow-wrap: anywhere;
	}

	.muted {
		color: var(--text-3);
		font-size: var(--text-sm);
	}
</style>
