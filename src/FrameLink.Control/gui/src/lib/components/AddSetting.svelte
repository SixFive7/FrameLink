<script lang="ts">
	/**
	 * Adds a setting that does not exist yet.
	 *
	 * The key field accepts **any** string. The datalist beside it offers the catalogued keys
	 * the server is not already holding, which is a convenience and nothing more — §3.4 says
	 * the settings list will grow, and a GUI that only lets an operator write keys it already
	 * knows about would make every future setting wait for a GUI release.
	 *
	 * That is also why the form is deliberately plain: it is a key box and a value box. The
	 * moment it becomes a dropdown of known settings, the mechanism has stopped being generic.
	 */
	import { suggestedKeys, describeSetting } from '$lib/settings-catalog';
	import { collapse } from '$lib/design/motion';
	import Button from './Button.svelte';
	import TextField from './TextField.svelte';

	interface Props {
		/** Keys already held, so they are not offered again. */
		existing: string[];
		/** Wording differs between the fleet screen and a device's overrides. */
		mode: 'fleet' | 'device';
		disabled?: boolean;
		onadd: (key: string, value: string) => Promise<void>;
	}

	let { existing, mode, disabled = false, onadd }: Props = $props();

	let open = $state(false);
	let key = $state('');
	let value = $state('');
	let busy = $state(false);

	const suggestions = $derived(suggestedKeys(existing));
	const meta = $derived(key ? describeSetting(key.trim()) : undefined);
	const valid = $derived(key.trim().length > 0);

	async function submit() {
		if (!valid) return;
		busy = true;
		try {
			await onadd(key.trim(), value);
			key = '';
			value = '';
			open = false;
		} finally {
			busy = false;
		}
	}
</script>

<div class="adder">
	{#if !open}
		<Button variant="ghost" icon="plus" {disabled} onclick={() => (open = true)}>
			{mode === 'fleet' ? 'Add a fleet setting' : 'Add an override'}
		</Button>
	{:else}
		<form
			class="form"
			transition:collapse
			onsubmit={(event) => {
				event.preventDefault();
				void submit();
			}}
		>
			<TextField
				bind:value={key}
				label="Key"
				placeholder="slideshow.album"
				mono
				autofocus
				list="setting-suggestions"
				size="sm"
			/>

			<TextField
				bind:value
				label="Value"
				kind={meta?.kind === 'secret' ? 'secret' : 'text'}
				placeholder={meta?.example ?? 'any string'}
				size="sm"
			/>

			<div class="actions">
				<Button type="submit" variant="primary" size="sm" icon="check" busy={busy} disabled={!valid}>
					Add
				</Button>
				<Button variant="quiet" size="sm" onclick={() => (open = false)}>Cancel</Button>
			</div>

			<datalist id="setting-suggestions">
				{#each suggestions as suggestion (suggestion)}
					<option value={suggestion}></option>
				{/each}
			</datalist>

			{#if meta}
				<p class="hint">
					{#if meta.known}{meta.hint}{:else}Anything is a valid key. This one is not in the
						interface's catalogue, so it will render with its raw name.{/if}
				</p>
			{/if}
		</form>
	{/if}
</div>

<style>
	.adder {
		margin-top: var(--space-2);
	}

	.form {
		display: grid;
		grid-template-columns: minmax(12rem, 1fr) minmax(12rem, 1.4fr) auto;
		align-items: end;
		gap: var(--space-3);
		padding: var(--space-4) var(--space-5);
		border-radius: var(--radius-md);
		border: 1px dashed var(--line-strong);
		background: var(--surface-1);
	}

	.actions {
		display: flex;
		gap: var(--space-2);
		padding-bottom: 2px;
	}

	.hint {
		grid-column: 1 / -1;
		font-size: var(--text-xs);
		color: var(--text-3);
	}

	@media (max-width: 48rem) {
		.form {
			grid-template-columns: 1fr;
		}
	}
</style>
