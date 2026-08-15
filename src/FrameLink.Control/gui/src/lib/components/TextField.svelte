<script lang="ts">
	/**
	 * A labelled input.
	 *
	 * The floating focus treatment is a two-layer ring rather than a border colour change, so
	 * the field does not shift by a pixel when it gains focus — a form that twitches while
	 * you tab through it feels cheap, and this app's most important form is the one where an
	 * operator types a very long password.
	 *
	 * `kind: 'secret'` adds a reveal toggle. Passwords and API keys are typed once and
	 * verified by eye; hiding them with no way to check is how a 40-character passphrase gets
	 * entered wrong three times.
	 */
	import Icon from './Icon.svelte';

	interface Props {
		value: string;
		label?: string;
		hint?: string;
		placeholder?: string;
		kind?: 'text' | 'secret' | 'number' | 'url' | 'time';
		mono?: boolean;
		autofocus?: boolean;
		disabled?: boolean;
		invalid?: boolean;
		id?: string;
		name?: string;
		autocomplete?: AutoFill;
		list?: string;
		size?: 'sm' | 'md' | 'lg';
		onkeydown?: (event: KeyboardEvent) => void;
		oninput?: () => void;
	}

	let {
		value = $bindable(),
		label,
		hint,
		placeholder,
		kind = 'text',
		mono = false,
		autofocus = false,
		disabled = false,
		invalid = false,
		id,
		name,
		autocomplete,
		list,
		size = 'md',
		onkeydown,
		oninput
	}: Props = $props();

	let revealed = $state(false);

	// One stable id per instance, so the label/hint association survives a re-render. The
	// fallback is generated once rather than derived, because a derived id would change under
	// the label the moment anything else in the component updated.
	const generatedId = `field-${Math.random().toString(36).slice(2, 9)}`;
	const fieldId = $derived(id ?? generatedId);
	const inputType = $derived(
		kind === 'secret' ? (revealed ? 'text' : 'password') : kind === 'url' ? 'url' : kind
	);

	// A secret that is not the login is an API key in a settings row, not a credential for
	// this site. Left to its default, Chrome offers to save it as the operator's password for
	// the Fleet Manager — so anything secret opts out unless the caller says otherwise.
	const resolvedAutocomplete = $derived(autocomplete ?? (kind === 'secret' ? 'off' : undefined));
</script>

<div class="field {size}" class:invalid class:disabled>
	{#if label}
		<label for={fieldId}>{label}</label>
	{/if}

	<div class="shell">
		<!-- svelte-ignore a11y_autofocus -- deliberate: these are single-purpose screens whose
		     only sensible first action is typing in this field -->
		<input
			id={fieldId}
			{name}
			type={inputType}
			class:mono
			bind:value
			{placeholder}
			{disabled}
			{autofocus}
			autocomplete={resolvedAutocomplete}
			{list}
			aria-invalid={invalid}
			aria-describedby={hint ? `${fieldId}-hint` : undefined}
			{onkeydown}
			{oninput}
		/>

		{#if kind === 'secret'}
			<button
				type="button"
				class="reveal"
				onclick={() => (revealed = !revealed)}
				aria-label={revealed ? 'Hide' : 'Show'}
				title={revealed ? 'Hide' : 'Show'}
			>
				<Icon name={revealed ? 'eyeOff' : 'eye'} size={16} />
			</button>
		{/if}
	</div>

	{#if hint}
		<p class="hint" id="{fieldId}-hint">{hint}</p>
	{/if}
</div>

<style>
	.field {
		display: grid;
		gap: var(--space-2);
		min-width: 0;
	}

	label {
		font-size: var(--text-xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.shell {
		position: relative;
		display: flex;
		align-items: center;
		border-radius: var(--radius-sm);
		background: var(--surface-sunken);
		border: 1px solid var(--line);
		transition:
			border-color var(--dur-quick) var(--ease-standard),
			box-shadow var(--dur-quick) var(--ease-standard),
			background-color var(--dur-quick) var(--ease-standard);
	}

	.shell:hover {
		border-color: var(--line-strong);
	}

	.shell:focus-within {
		border-color: var(--accent);
		background: var(--surface-2);
		box-shadow: 0 0 0 3px var(--accent-soft);
	}

	.invalid .shell,
	.invalid .shell:focus-within {
		border-color: var(--danger);
		box-shadow: 0 0 0 3px var(--danger-soft);
	}

	input {
		flex: 1;
		min-width: 0;
		background: none;
		border: 0;
		color: var(--text-1);
		font-size: var(--text-md);
		padding: var(--space-3) var(--space-4);
	}

	input:focus {
		outline: none;
	}

	input::placeholder {
		color: var(--text-3);
		opacity: 0.7;
	}

	.mono {
		font-family: var(--font-mono);
		letter-spacing: var(--track-code);
		font-size: var(--text-sm);
	}

	.sm input {
		padding: var(--space-2) var(--space-3);
		font-size: var(--text-sm);
	}
	.lg input {
		padding: var(--space-4) var(--space-5);
		font-size: var(--text-lg);
	}

	.disabled .shell {
		opacity: 0.55;
	}

	.reveal {
		display: grid;
		place-items: center;
		padding: var(--space-2);
		margin-right: var(--space-2);
		border-radius: var(--radius-xs);
		color: var(--text-3);
		transition:
			color var(--dur-quick) var(--ease-standard),
			background-color var(--dur-quick) var(--ease-standard);
	}
	.reveal:hover {
		color: var(--text-1);
		background: var(--surface-3);
	}

	.hint {
		font-size: var(--text-xs);
		color: var(--text-3);
		line-height: var(--leading-snug);
	}
</style>
