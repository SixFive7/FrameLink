<script lang="ts">
	/**
	 * The unconfigured Fleet Manager (§3.2).
	 *
	 * "An unconfigured instance explains itself rather than failing silently." A
	 * server-rendered version of this page already exists in `SetupPage.cs` for the case where
	 * no GUI has been built into the image; this is the same content, with the same variable
	 * name and the same Compose fragment, given the room to be read properly.
	 *
	 * Everything on it comes from `GET /api/status` — the variable name, the problem sentence
	 * and the Compose example are all the server's own words (`SetupStatus`), because a page
	 * that names a *different* variable than the server checks is worse than no page at all.
	 *
	 * The third section is the one the server-rendered fallback can only gesture at: what the
	 * frames are being told right now. The operator standing here is usually holding one, and
	 * the sentence on its screen is a diagnostic for this server.
	 */
	import { session } from '$lib/stores/session.svelte';
	import { rise, stagger } from '$lib/design/motion';
	import Button from '$lib/components/Button.svelte';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import CodeBlock from '$lib/components/CodeBlock.svelte';
	import Icon from '$lib/components/Icon.svelte';

	const setup = $derived(session.setup);
	const variable = $derived(setup?.variable ?? 'FRAMELINK_OPERATOR_PASSWORD');

	let rechecking = $state(false);

	async function recheck() {
		rechecking = true;
		await session.bootstrap();
		rechecking = false;
	}
</script>

<div class="page">
	<div class="column">
		<div in:rise={{ y: 16, duration: 720 }}>
			<Chip tone="warn" icon="alert">Not set up yet</Chip>
			<h1>This Fleet Manager has no operator password</h1>
			<p class="lead">
				{setup?.problem ??
					`The environment variable ${variable} is not set, so this Fleet Manager has no operator password and cannot adopt any device yet.`}
			</p>
		</div>

		<div in:rise={{ y: 16, delay: stagger(1, 4) }}>
			<Card>
				<h2>Set one variable and restart</h2>
				<p>
					The password lives in the environment and nowhere else — there is no user account,
					no password file and no setup wizard that writes a hash to disk. The variable is
					the credential, which means your Compose file is the single place it exists and
					rotating it is a container restart.
				</p>

				<p class="variable-line">
					The variable is named <code class="var">{variable}</code>, and it must be at least
					24 characters.
				</p>

				{#if setup?.composeExample}
					<CodeBlock code={setup.composeExample} label="docker-compose.yml" />
				{/if}

				<p class="footnote">
					<Icon name="key" size={13} />
					Choose a long passphrase. This server is reachable from the internet, and it is the
					only credential there is.
				</p>
			</Card>
		</div>

		<div in:rise={{ y: 16, delay: stagger(2, 4) }}>
			<Card tone="accent">
				<h2>What your frames are seeing right now</h2>
				<p>
					Any frame already pointed at this address has connected, been answered
					<code>not-configured</code>, and is showing this on its own screen:
				</p>

				<blockquote>
					<span class="quote-mark" aria-hidden="true"></span>
					Connected to a Fleet Manager, but it is not set up yet
				</blockquote>

				<p class="footnote">
					That is deliberate. The person setting up the server is usually the person holding
					the first frame, so the frame becomes a diagnostic for the server. Nothing is lost
					in the meantime — each frame that connects is already recorded, and the moment you
					set the password they are waiting in the adoption queue.
				</p>
			</Card>
		</div>

		<div class="recheck" in:rise={{ y: 16, delay: stagger(3, 4) }}>
			<Button variant="primary" size="lg" icon="refresh" busy={rechecking} onclick={recheck}>
				I have set it — check again
			</Button>
			<span>This page does not poll. Restart the container, then press it.</span>
		</div>
	</div>
</div>

<style>
	.page {
		min-height: 100dvh;
		display: grid;
		place-items: center;
		padding: var(--space-12) var(--space-6);
	}

	.column {
		width: 100%;
		max-width: 48rem;
		display: grid;
		gap: var(--space-6);
	}

	h1 {
		font-size: var(--text-3xl);
		margin: var(--space-4) 0 var(--space-3);
	}

	h2 {
		font-size: var(--text-lg);
		margin-bottom: var(--space-3);
	}

	.lead {
		font-size: var(--text-lg);
		color: var(--text-2);
		line-height: var(--leading-normal);
	}

	p {
		color: var(--text-2);
		font-size: var(--text-sm);
		line-height: var(--leading-normal);
	}

	.variable-line {
		margin: var(--space-4) 0;
		color: var(--text-1);
	}

	code {
		color: var(--info);
		background: var(--info-soft);
		padding: 0.1em 0.4em;
		border-radius: var(--radius-xs);
	}

	.var {
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-code);
	}

	blockquote {
		position: relative;
		margin: var(--space-5) 0;
		padding: var(--space-5) var(--space-6);
		border-radius: var(--radius-md);
		border: 1px solid var(--accent-line);
		background: var(--surface-sunken);
		font-size: var(--text-lg);
		font-weight: var(--weight-medium);
		color: var(--text-1);
		letter-spacing: var(--track-snug);
		text-align: center;
		overflow: hidden;
	}

	/* A slow warm bloom behind the sentence the frames are showing — the same warmth the
	   frame's own screen has behind it. */
	.quote-mark {
		position: absolute;
		inset: -40% -10% auto;
		height: 160%;
		background: radial-gradient(50% 50% at 50% 50%, var(--accent-soft), transparent 70%);
		animation: bloom 6s var(--ease-glide) infinite alternate;
	}

	@keyframes bloom {
		from {
			opacity: 0.5;
			transform: scale(0.9);
		}
		to {
			opacity: 1;
			transform: scale(1.1);
		}
	}

	.footnote {
		display: flex;
		align-items: flex-start;
		gap: var(--space-2);
		font-size: var(--text-xs);
		color: var(--text-3);
	}

	.recheck {
		display: flex;
		align-items: center;
		gap: var(--space-4);
		flex-wrap: wrap;
	}

	.recheck span {
		font-size: var(--text-xs);
		color: var(--text-3);
	}

	@media (prefers-reduced-motion: reduce) {
		.quote-mark {
			animation: none;
		}
	}
</style>
