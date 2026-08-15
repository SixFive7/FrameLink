<script lang="ts">
	/**
	 * One password field. Nothing else.
	 *
	 * §3.2: "Single operator, one very long password, from an environment variable only. No
	 * user accounts, no roles." So there is no username, no remember-me, no forgot-password
	 * and no sign-up — every one of those would be a lie about how this server works.
	 *
	 * What the screen does add is a reveal toggle, because a 40-character passphrase typed
	 * blind and rejected teaches an operator nothing. And it shows the server's own rejection
	 * sentence rather than a generic one: `OperatorEndpoints.SignIn` answers 401 with "That is
	 * not the operator password", and 503 `not-configured` when there is no password at all —
	 * two very different problems that a single "login failed" would flatten into one.
	 */
	import { ApiError } from '$lib/api/client';
	import { session } from '$lib/stores/session.svelte';
	import { rise, pop } from '$lib/design/motion';
	import Button from '$lib/components/Button.svelte';
	import Icon from '$lib/components/Icon.svelte';
	import TextField from '$lib/components/TextField.svelte';

	let password = $state('');
	let busy = $state(false);
	let problem = $state<string | undefined>();
	let shake = $state(0);

	async function submit() {
		if (!password || busy) return;
		busy = true;
		problem = undefined;

		try {
			await session.signIn(password);
		} catch (cause) {
			problem =
				cause instanceof ApiError
					? cause.message
					: 'The Fleet Manager did not answer. It may be restarting.';
			shake++;
			password = '';
		} finally {
			busy = false;
		}
	}
</script>

<div class="page">
	<form
		class="panel"
		in:rise={{ y: 18, duration: 720, scale: 0.02 }}
		onsubmit={(event) => {
			event.preventDefault();
			void submit();
		}}
	>
		<div class="mark" aria-hidden="true">
			<Icon name="frame" size={26} />
		</div>

		<h1>FrameLink</h1>
		<p class="sub">Fleet Manager</p>

		{#key shake}
			<div class="field" class:shake={shake > 0}>
				<TextField
					bind:value={password}
					kind="secret"
					label="Operator password"
					placeholder="your passphrase"
					autocomplete="current-password"
					size="lg"
					autofocus
					invalid={Boolean(problem)}
				/>
			</div>
		{/key}

		{#if problem}
			<p class="problem" role="alert" in:pop>
				<Icon name="alert" size={14} />
				{problem}
			</p>
		{/if}

		<Button type="submit" variant="primary" size="lg" full {busy} disabled={!password}>
			Sign in
		</Button>

		<p class="note">
			There is one operator and one password, read from the environment. No accounts, no
			roles, no recovery — rotating it means changing the variable and restarting the
			container.
		</p>
	</form>
</div>

<style>
	.page {
		min-height: 100dvh;
		display: grid;
		place-items: center;
		padding: var(--space-8) var(--space-6);
	}

	.panel {
		width: 100%;
		max-width: 25rem;
		display: grid;
		gap: var(--space-4);
		padding: var(--space-10) var(--space-8);
		border-radius: var(--radius-xl);
		border: 1px solid var(--line);
		background: var(--surface-glass);
		backdrop-filter: blur(20px) saturate(160%);
		box-shadow: var(--shadow-4);
		text-align: center;
	}

	.mark {
		justify-self: center;
		display: grid;
		place-items: center;
		width: 56px;
		height: 56px;
		border-radius: var(--radius-lg);
		color: var(--text-on-accent);
		background: linear-gradient(160deg, var(--accent-strong), var(--accent));
		box-shadow: var(--shadow-accent);
		animation: settle var(--dur-grand) var(--ease-spring) backwards;
	}

	@keyframes settle {
		from {
			opacity: 0;
			transform: translateY(-10px) scale(0.85) rotate(-8deg);
		}
	}

	h1 {
		font-size: var(--text-2xl);
		letter-spacing: var(--track-tight);
	}

	.sub {
		margin-top: -0.4rem;
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.field {
		text-align: left;
		margin-top: var(--space-3);
	}

	/* A wrong password shakes the field once. The single most useful piece of motion in the
	   app: it says "that was read and rejected" before the sentence below has been read. */
	.shake {
		animation: shake 420ms var(--ease-standard);
	}

	@keyframes shake {
		10%,
		90% {
			transform: translateX(-2px);
		}
		20%,
		80% {
			transform: translateX(4px);
		}
		30%,
		50%,
		70% {
			transform: translateX(-7px);
		}
		40%,
		60% {
			transform: translateX(7px);
		}
	}

	.problem {
		display: flex;
		align-items: center;
		justify-content: center;
		gap: var(--space-2);
		font-size: var(--text-xs);
		color: var(--danger);
		text-align: left;
	}

	.note {
		font-size: var(--text-xs);
		color: var(--text-3);
		line-height: var(--leading-snug);
		margin-top: var(--space-2);
	}

	@media (prefers-reduced-motion: reduce) {
		.mark,
		.shake {
			animation: none;
		}
	}
</style>
