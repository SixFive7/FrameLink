<script lang="ts">
	/**
	 * One frame's microphone firmware: what it runs, what the fleet converges on, and the one
	 * deliberate act that turns the first into the second (decision 91).
	 *
	 * **Nothing on this screen is typed into the authorisation.** The operator presses a button;
	 * the server composes `<sha256>:<ticket>` from the pinned image and the device id in the route
	 * it was called on. There is no field for a digest and no field for a frame, because the two
	 * values that decide *what gets written* and *which unit it gets written to* are the two an
	 * operator must not be able to mistype. The optional note is free text and reaches neither.
	 *
	 * **The attended path is the default and the bypass is not a button.** Asking the household is
	 * a primary button; authorising with nobody at the frame is a quiet link that opens a wider
	 * dialog, renders the frame's own four warnings verbatim, shows the exact token that will be
	 * written and which frame it names, and keeps its confirm shut until the acceptance is ticked.
	 * The asymmetry is the design: mains loss during the write is unguardable at the device and
	 * destroys the unit, and for an attended write the only mitigation that exists is the person in
	 * the room. Taking the bypass removes them, so the acceptance is what replaces them.
	 *
	 * **The standing is the frame's own sentence, never a paraphrase.** `detail` is whatever the
	 * agent last said about this frame — which interlock stopped it and what to do about it, or
	 * both firmware readings and how long the write took. This component decides the colour and
	 * the heading; the words are the frame's.
	 */
	import { onMount } from 'svelte';
	import { api, ApiError } from '$lib/api/client';
	import type { ArrayFlashStatusResponse } from '$lib/api/types';
	import { describeFlashPhase, describeRefusal, firmwareVersion, shortDigest } from '$lib/array-flash';
	import { describeEvent } from '$lib/reconcile';
	import { timeAgo, timeExact } from '$lib/format';
	import { toasts } from '$lib/stores/toast.svelte';
	import { collapse } from '$lib/design/motion';
	import Button from '$lib/components/Button.svelte';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import CodeBlock from '$lib/components/CodeBlock.svelte';
	import ConfirmDialog from '$lib/components/ConfirmDialog.svelte';
	import Icon from '$lib/components/Icon.svelte';
	import TextField from '$lib/components/TextField.svelte';

	interface Props {
		deviceId: string;
	}

	let { deviceId }: Props = $props();

	let view = $state<ArrayFlashStatusResponse | undefined>();
	let problem = $state<string | undefined>();
	let loading = $state(true);
	let busy = $state(false);

	let note = $state('');
	let askHousehold = $state(false);
	let askUnattended = $state(false);
	/** The acknowledgement. Reset every time the dialog opens, never remembered. */
	let accepted = $state(false);
	let trail = $state(false);

	const look = $derived(view ? describeFlashPhase(view) : undefined);
	const armed = $derived(view?.authorisation);
	const bypassToken = $derived(view ? `${view.unattendedPrefix}${view.deviceId}` : '');

	async function load() {
		try {
			view = await api.arrayFlash(deviceId);
			problem = undefined;
		} catch (cause) {
			problem =
				cause instanceof ApiError
					? cause.message
					: 'This frame’s microphone firmware could not be read.';
		} finally {
			loading = false;
		}
	}

	function openBypass() {
		accepted = false;
		askUnattended = true;
	}

	async function authorise(unattended: boolean) {
		busy = true;
		try {
			view = await api.authoriseArrayFlash(deviceId, {
				unattended,
				acknowledged: unattended ? accepted : false,
				note: note.trim() === '' ? undefined : note.trim()
			});
			note = '';
			toasts.ok(
				unattended ? 'Authorised, unattended' : 'Authorised',
				unattended
					? 'One write on this frame, with nobody asked. It is spent the instant the write starts.'
					: 'One write on this frame, once somebody standing at it agrees on the screen.'
			);
		} catch (cause) {
			toasts.error('Could not authorise a firmware write', cause);
		} finally {
			busy = false;
			askHousehold = false;
			askUnattended = false;
			accepted = false;
		}
	}

	async function withdraw() {
		busy = true;
		try {
			view = await api.withdrawArrayFlash(deviceId);
			toasts.ok(
				'Authorisation withdrawn',
				'This reaches a write that has not started. One already begun is not touched by it.'
			);
		} catch (cause) {
			toasts.error('Could not withdraw the authorisation', cause);
		} finally {
			busy = false;
		}
	}

	onMount(load);
</script>

<Card>
	<div class="head">
		<h2>Microphone firmware</h2>
		{#if view && look}
			<Chip tone={look.tone} icon={look.icon} size="sm">{look.label}</Chip>
		{/if}
	</div>

	{#if problem}
		<p class="problem">{problem}</p>
	{:else if loading}
		<p class="muted">Reading…</p>
	{:else if view}
		<dl class="facts">
			<div>
				<dt>Running now</dt>
				<dd>
					{#if view.runningFirmware}
						{view.runningFirmware}
						{#if view.runningFirmwareUtc}
							<span class="when" title={timeExact(view.runningFirmwareUtc)}>
								read {timeAgo(view.runningFirmwareUtc)}
							</span>
						{/if}
					{:else}
						<span class="muted">
							This frame has not reported its microphone unit yet. It says so at startup and again
							whenever the reading changes.
						</span>
					{/if}
				</dd>
			</div>
			<div>
				<dt>The fleet converges on</dt>
				<dd>
					firmware <b>{firmwareVersion(view.target.version)}</b>
					<span class="muted">— {view.target.name}</span>
					<code title={view.target.sha256}>{shortDigest(view.target.sha256)}</code>
				</dd>
			</div>
		</dl>

		<div class="standing {look?.tone ?? 'muted'}">
			<Icon name={look?.icon ?? 'info'} size={16} />
			<div>
				<b>
					{look?.label}{#if view.refusal}<span class="refusal"> — {describeRefusal(view.refusal)}</span>{/if}
				</b>
				<p>{view.detail}</p>
				{#if view.reportedUtc}
					<span class="when" title={timeExact(view.reportedUtc)}>
						the frame said so {timeAgo(view.reportedUtc)}
					</span>
				{/if}
			</div>
		</div>

		{#if armed}
			<div class="armed" class:unattended={armed.unattended}>
				<div class="armed-head">
					{#if armed.unattended}
						<Chip tone="danger" icon="alert" size="sm">Nobody at the frame will be asked</Chip>
					{:else}
						<Chip tone="ok" icon="shieldCheck" size="sm">The frame asks somebody first</Chip>
					{/if}
					{#if armed.issuedUtc}
						<Chip tone="muted" size="sm" title={timeExact(armed.issuedUtc)}>
							armed {timeAgo(armed.issuedUtc)}
						</Chip>
					{/if}
					<Button
						variant="quiet"
						icon="x"
						{busy}
						title="Withdraw this authorisation"
						onclick={withdraw}
					>
						Withdraw
					</Button>
				</div>

				{#if armed.note}
					<p class="note">“{armed.note}”</p>
				{/if}

				{#if !armed.namesTheTarget}
					<p class="caution">
						This value names a different image from the one this Fleet Manager pins, so the frame
						will refuse it. It was not composed here — clear it and authorise again.
					</p>
				{/if}

				{#if armed.unattendedDeviceId && !armed.unattended}
					<p class="caution">
						The bypass inside this value names <code>{armed.unattendedDeviceId}</code>, which is
						not this frame. This frame ignores it and asks somebody standing at it, exactly as it
						would without it.
					</p>
				{/if}

				<CodeBlock code={armed.value} label="What this frame reads" />
			</div>
		{:else if view.adopted}
			<div class="offer">
				<TextField
					bind:value={note}
					label="Note for the record (optional)"
					placeholder="why this frame, and who asked for it"
					hint="Goes into the authorisation, so it is still readable months later."
				/>
				<div class="offer-actions">
					<Button variant="primary" icon="shieldCheck" onclick={() => (askHousehold = true)}>
						Authorise a write
					</Button>
					<button class="bypass" type="button" onclick={openBypass}>
						Authorise with nobody at the frame…
					</button>
				</div>
			</div>
		{:else}
			<div class="locked">
				<Icon name="key" size={16} />
				<p>
					Only an adopted frame can be authorised: a frame that has not been adopted receives no
					settings at all, and an authorisation is a setting.
				</p>
			</div>
		{/if}

		{#if view.events.length > 0}
			<button class="more" type="button" onclick={() => (trail = !trail)}>
				{trail ? 'Hide' : `Show what this frame has said (${view.events.length})`}
			</button>
			{#if trail}
				<ol class="timeline" transition:collapse>
					{#each view.events as moment, index (`${moment.occurredUtc}-${index}`)}
						{@const kind = describeEvent(moment.kind)}
						<li class={kind.tone}>
							<div class="when">
								<span class="kind"><Icon name={kind.icon} size={12} /> {kind.label}</span>
								<span title={timeExact(moment.occurredUtc)}>{timeAgo(moment.occurredUtc)}</span>
							</div>
							<p class="summary">{moment.summary}</p>
							{#if moment.delta}<pre class="delta">{moment.delta}</pre>{/if}
						</li>
					{/each}
				</ol>
			{/if}
		{/if}
	{/if}
</Card>

<ConfirmDialog
	bind:open={askHousehold}
	title="Authorise one firmware write?"
	confirmLabel="Authorise it"
	tone="primary"
	{busy}
	onconfirm={() => void authorise(false)}
	oncancel={() => (askHousehold = false)}
>
	This frame will ask somebody standing at it to agree on its own screen, and will not write
	anything until they do. The authorisation stays armed until then, and is spent the instant the
	write starts — it authorises one write, on this frame, and nothing afterwards.
</ConfirmDialog>

<ConfirmDialog
	bind:open={askUnattended}
	title="Write firmware with nobody at this frame?"
	confirmLabel="I accept this — authorise it"
	cancelLabel="No, ask the household"
	tone="danger"
	wide
	{busy}
	confirmDisabled={!accepted}
	onconfirm={() => void authorise(true)}
	oncancel={() => (askUnattended = false)}
>
	<!-- The frame's own words, served by the Fleet Manager and rendered unchanged. The agent
	     emits these same sentences into the audit event of every unattended write, so what the
	     operator accepted here and what the record says they accepted are one text. -->
	<ul class="warning">
		{#each view?.unattendedWarning ?? [] as line, index (index)}
			<li>{line}</li>
		{/each}
	</ul>

	<p class="scope">
		It applies to <b>{view?.deviceId}</b> and to no other frame. This is the word that will be
		written into the authorisation:
	</p>
	<code class="token">{bypassToken}</code>

	<label class="accept">
		<input type="checkbox" bind:checked={accepted} />
		<span>I have read the four points above and accept them for this frame.</span>
	</label>
</ConfirmDialog>

<style>
	.head {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		margin-bottom: var(--space-4);
	}

	.head h2 {
		font-size: var(--text-lg);
		margin: 0;
		margin-right: auto;
	}

	.facts {
		display: grid;
		gap: var(--space-3);
		margin: 0 0 var(--space-4);
		font-size: var(--text-sm);
	}

	.facts dt {
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
		margin-bottom: var(--space-1);
	}

	.facts dd {
		margin: 0;
		color: var(--text-2);
		line-height: var(--leading-normal);
	}

	.facts code {
		color: var(--text-3);
	}

	.when {
		color: var(--text-3);
		font-size: var(--text-xs);
		white-space: nowrap;
	}

	.standing {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		padding: var(--space-4) var(--space-5);
		border-radius: var(--radius-md);
		border: 1px solid var(--note-line);
		background: var(--note-fill);
		color: var(--text-2);
		font-size: var(--text-sm);
		line-height: var(--leading-snug);
	}

	.standing b {
		color: var(--note-ink);
	}

	.standing p {
		margin: var(--space-2) 0 0;
	}

	.standing :global(.icon) {
		color: var(--note-ink);
		margin-top: 2px;
	}

	.standing .refusal {
		font-weight: var(--weight-regular, 400);
		color: var(--text-2);
	}

	.standing.ok {
		--note-ink: var(--ok);
		--note-fill: var(--ok-soft);
		--note-line: var(--ok-line);
	}
	.standing.warn {
		--note-ink: var(--accent);
		--note-fill: var(--accent-soft);
		--note-line: var(--accent-line);
	}
	.standing.danger {
		--note-ink: var(--danger);
		--note-fill: var(--danger-soft);
		--note-line: var(--danger-line);
	}
	.standing.info,
	.standing.tech {
		--note-ink: var(--info);
		--note-fill: var(--info-soft);
		--note-line: var(--info-line);
	}
	.standing.muted {
		--note-ink: var(--text-1);
		--note-fill: var(--surface-sunken);
		--note-line: var(--line);
	}

	.armed {
		display: grid;
		gap: var(--space-3);
		margin-top: var(--space-4);
		padding: var(--space-4);
		border-radius: var(--radius-md);
		border: 1px solid var(--accent-line);
		background: var(--accent-soft);
	}

	.armed.unattended {
		border-color: var(--danger-line);
		background: var(--danger-soft);
	}

	.armed-head {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		flex-wrap: wrap;
	}

	.armed-head :global(button) {
		margin-left: auto;
	}

	.note {
		margin: 0;
		font-size: var(--text-sm);
		color: var(--text-2);
		font-style: italic;
	}

	.caution {
		margin: 0;
		font-size: var(--text-sm);
		color: var(--danger);
		line-height: var(--leading-normal);
	}

	.offer {
		display: grid;
		gap: var(--space-4);
		margin-top: var(--space-4);
	}

	.offer-actions {
		display: flex;
		align-items: center;
		gap: var(--space-4);
		flex-wrap: wrap;
	}

	/* Deliberately not a Button. The bypass is reachable in one click and looks like nothing —
	   it is the dialog behind it that does the work, and a second solid button beside the safe
	   one would read as a second ordinary choice. */
	.bypass {
		font-size: var(--text-xs);
		color: var(--text-3);
		text-decoration: underline;
		text-underline-offset: 3px;
		transition: color var(--dur-quick) var(--ease-standard);
	}

	.bypass:hover {
		color: var(--danger);
	}

	.locked {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		margin-top: var(--space-4);
		padding: var(--space-4);
		border-radius: var(--radius-md);
		border: 1px dashed var(--line-strong);
		background: var(--surface-sunken);
		color: var(--text-2);
		font-size: var(--text-xs);
		line-height: var(--leading-normal);
	}

	.locked p {
		margin: 0;
	}

	.more {
		margin-top: var(--space-4);
		font-size: var(--text-xs);
		color: var(--text-3);
		transition: color var(--dur-quick) var(--ease-standard);
	}

	.more:hover {
		color: var(--text-1);
	}

	.timeline {
		list-style: none;
		display: grid;
		gap: var(--space-3);
		margin: var(--space-3) 0 0;
		padding: 0;
	}

	.timeline li {
		padding-left: var(--space-4);
		border-left: 2px solid var(--line);
	}

	.timeline li.warn {
		border-left-color: var(--accent);
	}
	.timeline li.danger {
		border-left-color: var(--danger);
	}
	.timeline li.ok {
		border-left-color: var(--ok);
	}
	.timeline li.info {
		border-left-color: var(--info);
	}

	.timeline .when {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		font-size: var(--text-2xs);
		color: var(--text-3);
	}

	.timeline .kind {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		text-transform: uppercase;
		letter-spacing: var(--track-caps);
		font-weight: var(--weight-semibold);
	}

	.timeline .summary {
		margin: var(--space-1) 0 0;
		font-size: var(--text-sm);
		color: var(--text-2);
		line-height: var(--leading-snug);
	}

	/* `dfu-util`'s output verbatim, newlines and all — it is the only record of what the device
	   said while it was being written, so it is not collapsed into a line. */
	.timeline .delta {
		margin: var(--space-2) 0 0;
		padding: var(--space-2) var(--space-3);
		border-radius: var(--radius-xs);
		background: var(--surface-sunken);
		color: var(--text-3);
		font-size: var(--text-2xs);
		white-space: pre-wrap;
		overflow-x: auto;
	}

	.warning {
		display: grid;
		gap: var(--space-3);
		margin: 0;
		padding-left: var(--space-5);
	}

	.warning li {
		color: var(--text-1);
		line-height: var(--leading-normal);
	}

	.scope {
		margin: var(--space-4) 0 var(--space-2);
	}

	.token {
		display: block;
		padding: var(--space-2) var(--space-3);
		border-radius: var(--radius-xs);
		border: 1px solid var(--danger-line);
		background: var(--surface-sunken);
		color: var(--danger);
		font-size: var(--text-2xs);
		word-break: break-all;
	}

	.accept {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		margin-top: var(--space-4);
		color: var(--text-1);
		cursor: pointer;
	}

	.accept input {
		margin-top: 2px;
		width: 1.05rem;
		height: 1.05rem;
		accent-color: var(--danger);
		flex: none;
	}

	.muted {
		color: var(--text-3);
	}

	.problem {
		color: var(--danger);
		font-size: var(--text-sm);
	}
</style>
