<script lang="ts">
	/**
	 * A modal confirmation, on the native `<dialog>` element so focus trapping, Escape and
	 * the top layer come from the platform rather than from a hand-rolled focus manager.
	 *
	 * Used for the two destructive actions the GUI offers: blocking a device, and forgetting
	 * one. §3.3 calls decommissioning "a confirmed, destructive action", and blocking stops a
	 * frame's product immediately (`OperatorEndpoints.BlockAsync` closes the socket), so both
	 * deserve a beat of friction.
	 *
	 * The panel scales in from 96% rather than sliding: a modal that arrives from a direction
	 * implies it came from somewhere, and this one did not.
	 */
	import type { Snippet } from 'svelte';
	import Button from './Button.svelte';

	interface Props {
		open: boolean;
		title: string;
		confirmLabel?: string;
		cancelLabel?: string;
		tone?: 'danger' | 'primary';
		busy?: boolean;
		/**
		 * Holds the confirm button shut while the body still has something the operator has to
		 * do — currently one dialog, the unattended firmware write, where the acceptance of the
		 * warnings *is* the decision and a dialog that could be dismissed with one click would
		 * make the most dangerous action in the product the cheapest one.
		 */
		confirmDisabled?: boolean;
		/** Widens the panel for a dialog whose body is more than two sentences. */
		wide?: boolean;
		onconfirm: () => void;
		oncancel: () => void;
		children?: Snippet;
	}

	let {
		open = $bindable(),
		title,
		confirmLabel = 'Confirm',
		cancelLabel = 'Cancel',
		tone = 'danger',
		busy = false,
		confirmDisabled = false,
		wide = false,
		onconfirm,
		oncancel,
		children
	}: Props = $props();

	let dialog = $state<HTMLDialogElement>();

	$effect(() => {
		if (!dialog) return;
		if (open && !dialog.open) dialog.showModal();
		if (!open && dialog.open) dialog.close();
	});
</script>

<dialog class:wide bind:this={dialog} oncancel={(event) => {
	event.preventDefault();
	oncancel();
}}>
	<div class="panel">
		<h2>{title}</h2>
		{#if children}<div class="body">{@render children()}</div>{/if}
		<div class="actions">
			<Button variant="ghost" onclick={oncancel}>{cancelLabel}</Button>
			<Button
				variant={tone === 'danger' ? 'danger' : 'primary'}
				{busy}
				disabled={confirmDisabled}
				onclick={onconfirm}
			>
				{confirmLabel}
			</Button>
		</div>
	</div>
</dialog>

<style>
	dialog {
		border: 0;
		padding: 0;
		background: none;
		color: inherit;
		max-width: min(28rem, calc(100vw - var(--space-8)));
	}

	dialog.wide {
		max-width: min(42rem, calc(100vw - var(--space-8)));
	}

	dialog::backdrop {
		background: rgb(3 5 9 / 0.62);
		backdrop-filter: blur(3px);
		animation: fade var(--dur-quick) var(--ease-standard);
	}

	dialog[open] .panel {
		animation: enter var(--dur-base) var(--ease-entrance);
	}

	.panel {
		border-radius: var(--radius-lg);
		border: 1px solid var(--line-strong);
		background: var(--surface-glass);
		backdrop-filter: blur(20px) saturate(160%);
		box-shadow: var(--shadow-4);
		padding: var(--space-6);
		display: grid;
		gap: var(--space-4);
	}

	h2 {
		font-size: var(--text-lg);
	}

	.body {
		font-size: var(--text-sm);
		color: var(--text-2);
		line-height: var(--leading-normal);
	}

	.actions {
		display: flex;
		justify-content: flex-end;
		gap: var(--space-3);
		margin-top: var(--space-1);
	}

	@keyframes fade {
		from {
			opacity: 0;
		}
	}

	@keyframes enter {
		from {
			opacity: 0;
			transform: scale(0.96);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		dialog::backdrop,
		dialog[open] .panel {
			animation: none;
		}
	}
</style>
