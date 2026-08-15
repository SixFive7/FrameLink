<script lang="ts">
	/**
	 * One setting, in either of the two contexts it appears in.
	 *
	 * §3.4: "Every setting is fleet-managed: a fleet default with a per-device override, the
	 * override always winning." The single hardest thing for this screen to communicate is
	 * *which of those two a value currently is*, because the consequence of getting it wrong
	 * is changing every frame in the house instead of one.
	 *
	 * The visual grammar, used identically everywhere a setting appears:
	 *
	 *  - **Inherited** — a chain-link glyph, the value in the muted tone, a left edge in the
	 *    line colour. It reads as "this is coming from somewhere else", because it is.
	 *  - **Overridden** — the accent left edge, an `Override` chip, the value at full
	 *    contrast, and the fleet default kept visible underneath in small muted type so the
	 *    thing being overridden is never hidden by the thing overriding it.
	 *  - **Fleet default (on the fleet screen)** — the accent edge, because on that screen the
	 *    fleet default *is* the authoritative value rather than the inherited one.
	 *
	 * Editing is in place. There is no save bar and no dirty-state modal: a row that has been
	 * changed shows its own Save and Cancel, and Enter saves. `PUT` is idempotent
	 * (`SetFleetDefaultAsync` upserts), so a double-press is harmless.
	 */
	import { describeSetting } from '$lib/settings-catalog';
	import { slip } from '$lib/design/motion';
	import Button from './Button.svelte';
	import Chip from './Chip.svelte';
	import Icon from './Icon.svelte';
	import TextField from './TextField.svelte';

	interface Props {
		settingKey: string;
		/** The fleet default, if there is one. */
		fleetValue?: string;
		/** The per-device override, if there is one. Absent on the fleet screen. */
		overrideValue?: string;
		/** `fleet` edits the default for everyone; `device` edits one frame's override. */
		mode: 'fleet' | 'device';
		/** Device rows are read-only until the device is adopted — the server answers 409. */
		locked?: boolean;
		onsave: (key: string, value: string) => Promise<void>;
		onremove: (key: string) => Promise<void>;
	}

	let {
		settingKey,
		fleetValue,
		overrideValue,
		mode,
		locked = false,
		onsave,
		onremove
	}: Props = $props();

	const meta = $derived(describeSetting(settingKey));
	const overridden = $derived(mode === 'device' && overrideValue !== undefined);

	/** What the device actually receives — the override if there is one, else the default. */
	const effective = $derived(overrideValue ?? fleetValue ?? '');

	let editing = $state(false);
	let draft = $state('');
	let busy = $state(false);

	function beginEdit(seed: string) {
		draft = seed;
		editing = true;
	}

	async function save() {
		busy = true;
		try {
			await onsave(settingKey, draft);
			editing = false;
		} finally {
			busy = false;
		}
	}

	async function revert() {
		busy = true;
		try {
			await onremove(settingKey);
			editing = false;
		} finally {
			busy = false;
		}
	}
</script>

<div class="setting" class:overridden class:inherited={mode === 'device' && !overridden}>
	<div class="head">
		<div class="naming">
			<h4>
				{meta.label}
				{#if !meta.known}
					<span class="raw" title="This interface has no description for this key">
						uncatalogued
					</span>
				{/if}
			</h4>
			<code class="key">{settingKey}</code>
		</div>

		<div class="tags">
			{#if mode === 'device'}
				{#if overridden}
					<Chip tone="warn" icon="pencil" size="sm">Override</Chip>
				{:else}
					<Chip tone="muted" icon="link" size="sm">Fleet default</Chip>
				{/if}
			{/if}
		</div>
	</div>

	{#if editing}
		<div class="editor" transition:slip={{ x: -8 }}>
			<TextField
				bind:value={draft}
				kind={meta.kind === 'duration' ? 'number' : meta.kind === 'boolean' ? 'text' : meta.kind}
				mono={meta.kind === 'secret' || !meta.known}
				placeholder={meta.example}
				size="sm"
				autofocus
				onkeydown={(event) => {
					if (event.key === 'Enter') void save();
					if (event.key === 'Escape') editing = false;
				}}
			/>
			<div class="editor-actions">
				<Button variant="primary" size="sm" icon="check" {busy} onclick={save}>Save</Button>
				<Button variant="quiet" size="sm" onclick={() => (editing = false)}>Cancel</Button>
			</div>
		</div>
	{:else}
		<div class="value-line">
			<span class="value" class:secret={meta.kind === 'secret'} class:empty={!effective}>
				{#if !effective}
					<em>not set</em>
				{:else if meta.kind === 'secret'}
					{'•'.repeat(Math.min(24, effective.length))}
				{:else}
					{effective}
				{/if}
			</span>

			{#if !locked}
				<div class="row-actions">
					<Button
						variant="quiet"
						size="sm"
						icon="pencil"
						onclick={() => beginEdit(effective)}
						aria-label="Edit {meta.label}"
					>
						{overridden || mode === 'fleet' ? 'Edit' : 'Override'}
					</Button>

					{#if overridden}
						<Button variant="quiet" size="sm" icon="refresh" {busy} onclick={revert}>
							Use fleet default
						</Button>
					{:else if mode === 'fleet' && fleetValue !== undefined}
						<Button
							variant="quiet"
							size="sm"
							icon="trash"
							{busy}
							onclick={revert}
							aria-label="Remove {meta.label}"
						>
							Remove
						</Button>
					{/if}
				</div>
			{/if}
		</div>

		{#if overridden}
			<p class="beneath">
				<Icon name="link" size={12} />
				Fleet default is
				<span class="beneath-value"
					>{fleetValue === undefined
						? 'not set'
						: meta.kind === 'secret'
							? '•'.repeat(Math.min(16, fleetValue.length))
							: fleetValue}</span
				>
			</p>
		{/if}
	{/if}

	<p class="hint">{meta.hint}</p>
</div>

<style>
	.setting {
		display: grid;
		gap: var(--space-2);
		padding: var(--space-4) var(--space-5);
		border-radius: var(--radius-md);
		border: 1px solid var(--line);
		border-left: 3px solid var(--edge, var(--line-strong));
		background: var(--surface-1);
		transition:
			border-color var(--dur-base) var(--ease-standard),
			background-color var(--dur-base) var(--ease-standard);
	}

	.inherited {
		--edge: var(--line-strong);
		background: transparent;
	}

	/* An overridden setting is the one thing on this screen with consequences for exactly one
	   frame. It gets the accent edge and a wash, so scanning a long list finds it instantly. */
	.overridden {
		--edge: var(--accent);
		background: linear-gradient(90deg, var(--accent-soft), transparent 42%), var(--surface-1);
		border-color: var(--accent-line);
	}

	.head {
		display: flex;
		align-items: flex-start;
		justify-content: space-between;
		gap: var(--space-4);
	}

	.naming {
		min-width: 0;
	}

	h4 {
		font-size: var(--text-sm);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-snug);
		display: flex;
		align-items: baseline;
		gap: var(--space-2);
	}

	.raw {
		font-size: var(--text-2xs);
		font-weight: var(--weight-normal);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.key {
		font-size: var(--text-2xs);
		color: var(--text-3);
		letter-spacing: var(--track-code);
	}

	.tags {
		flex: none;
	}

	.value-line {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: var(--space-4);
		min-height: 30px;
	}

	.value {
		font-size: var(--text-md);
		font-weight: var(--weight-medium);
		color: var(--text-1);
		overflow-wrap: anywhere;
		min-width: 0;
	}

	.inherited .value {
		color: var(--text-2);
		font-weight: var(--weight-normal);
	}

	.secret {
		font-family: var(--font-mono);
		letter-spacing: 0.16em;
	}

	.empty {
		color: var(--text-3);
	}

	.row-actions {
		display: flex;
		align-items: center;
		gap: var(--space-1);
		flex: none;
		opacity: 0.55;
		transition: opacity var(--dur-quick) var(--ease-standard);
	}

	.setting:hover .row-actions,
	.setting:focus-within .row-actions {
		opacity: 1;
	}

	.beneath {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		font-size: var(--text-xs);
		color: var(--text-3);
	}

	.beneath-value {
		font-family: var(--font-mono);
		letter-spacing: var(--track-code);
	}

	.editor {
		display: flex;
		align-items: flex-end;
		gap: var(--space-3);
		flex-wrap: wrap;
	}

	.editor :global(.field) {
		flex: 1;
		min-width: 14rem;
	}

	.editor-actions {
		display: flex;
		gap: var(--space-2);
	}

	.hint {
		font-size: var(--text-xs);
		color: var(--text-3);
		line-height: var(--leading-snug);
	}
</style>
