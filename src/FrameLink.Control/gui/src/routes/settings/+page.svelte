<script lang="ts">
	/**
	 * Fleet defaults.
	 *
	 * §3.4: "Every setting is fleet-managed: a fleet default with a per-device override…
	 * Not a fixed list but a generic mechanism, because the list will grow." This screen is
	 * built to that shape:
	 *
	 *  - it renders **whatever keys the server is holding**, obtained from `GET /api/settings`
	 *    and nothing else;
	 *  - the add form accepts **any** key;
	 *  - `settings-catalog.ts` supplies a friendly label and a sentence of help *when it
	 *    happens to recognise a key*, and is otherwise absent. Nothing on this screen depends
	 *    on a key being catalogued, and an uncatalogued key says so on its own row rather than
	 *    being hidden.
	 *
	 * A change here reaches every online device immediately: `SetFleetSettingAsync` calls
	 * `publisher.PushAllAsync`, and any device whose override still wins simply resolves to
	 * the value it already had and ignores the push. The revision counter in the header is the
	 * server's own, so it is the honest way to see that a write landed.
	 */
	import { onMount } from 'svelte';
	import { api } from '$lib/api/client';
	import type { FleetSettingsResponse } from '$lib/api/types';
	import { groupKeys } from '$lib/settings-catalog';
	import { rise, settle } from '$lib/design/motion';
	import { toasts } from '$lib/stores/toast.svelte';
	import AddSetting from '$lib/components/AddSetting.svelte';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';
	import SettingRow from '$lib/components/SettingRow.svelte';

	let data = $state<FleetSettingsResponse | undefined>();
	let loading = $state(true);
	let problem = $state<string | undefined>();

	const keys = $derived(Object.keys(data?.values ?? {}));
	const groups = $derived(groupKeys(keys));

	async function load() {
		try {
			data = await api.fleetSettings();
			problem = undefined;
		} catch (cause) {
			problem = cause instanceof Error ? cause.message : 'The settings could not be read.';
		} finally {
			loading = false;
		}
	}

	onMount(load);

	async function save(key: string, value: string) {
		try {
			await api.setFleetSetting(key, value);
			toasts.ok(`Saved ${key}`, 'Every online frame has been told.');
			await load();
		} catch (cause) {
			toasts.error(`Could not save ${key}`, cause);
		}
	}

	async function remove(key: string) {
		try {
			await api.removeFleetSetting(key);
			toasts.ok(`Removed ${key}`, 'Frames without an override for it now have no value at all.');
			await load();
		} catch (cause) {
			toasts.error(`Could not remove ${key}`, cause);
		}
	}
</script>

<div class="page">
	<header in:rise={{ y: 10 }}>
		<div>
			<h1>Fleet settings</h1>
			<p>
				The default for every frame. A device that has its own override for a key ignores
				what is here — the override always wins.
			</p>
		</div>
		{#if data}
			<Chip tone="tech" size="sm" title="Bumped by every settings write anywhere in the fleet">
				revision {data.revision}
			</Chip>
		{/if}
	</header>

	{#if loading}
		<Card><p class="muted">Reading the fleet defaults…</p></Card>
	{:else if problem}
		<Card tone="danger"><p>{problem}</p></Card>
	{:else if keys.length === 0}
		<Card padding="none">
			<EmptyState icon="sliders" title="No fleet defaults yet">
				Nothing has been set, so every adopted frame receives an empty settings payload. Add
				the values your fleet shares — the Immich server, the call room, the backlight
				schedule — and override the per-frame ones on each device.
			</EmptyState>
		</Card>
	{:else}
		{#each groups as group, index (group.group)}
			<section in:settle={{ index, count: groups.length }}>
				<h2>{group.group}</h2>
				<div class="rows">
					{#each group.keys as key (key)}
						<SettingRow
							settingKey={key}
							fleetValue={data?.values[key]}
							mode="fleet"
							onsave={save}
							onremove={remove}
						/>
					{/each}
				</div>
			</section>
		{/each}
	{/if}

	{#if !loading && !problem}
		<AddSetting existing={keys} mode="fleet" onadd={save} />
	{/if}

	<p class="footnote">
		Keys are opaque to the Fleet Manager — it stores a string against a name and never
		validates either. Anything the agent understands can be written here, including keys
		this interface has no description for.
	</p>
</div>

<style>
	/* Narrower than the fleet list on purpose. This screen is a form, and a value sitting
	   1200px away from the button that edits it is a table pretending to be one. */
	.page {
		display: grid;
		gap: var(--space-6);
		max-width: 58rem;
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
	}

	header p {
		margin-top: var(--space-2);
		max-width: 44rem;
		font-size: var(--text-sm);
		color: var(--text-2);
	}

	h2 {
		font-size: var(--text-sm);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
		margin-bottom: var(--space-3);
	}

	.rows {
		display: grid;
		gap: var(--space-3);
	}

	.muted {
		color: var(--text-3);
		font-size: var(--text-sm);
	}

	.footnote {
		font-size: var(--text-xs);
		color: var(--text-3);
		max-width: 46rem;
	}
</style>
