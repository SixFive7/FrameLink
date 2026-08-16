<script lang="ts">
	/**
	 * The resources waiting on something, drawn hanging off the thing they wait on.
	 *
	 * This is the shape of §2.2's DAG, and drawing it is the entire diagnostic value of the
	 * screen. On the first full provision of a real frame the report read *37 in sync, 1
	 * escalated, 12 blocked* — and the twelve were all downstream of the one escalation, each
	 * naming what it waited on. As a flat list of 78 rows that is twelve separate mysteries. As
	 * a tree it is one sentence: **this broke, and this is everything standing still because
	 * of it.**
	 *
	 * The rail is drawn rather than bulleted so it lines up with the border, the same way
	 * `PackagePanel`'s timeline does it. Depth is expressed by indentation and by nothing else —
	 * a blocked resource three levels down is not three times as bad, it is the same kind of
	 * consequence at a greater distance from the cause.
	 *
	 * The component recurses by importing itself; the tree is a DAG walked with a cycle guard in
	 * `blockedBehind`, so the recursion terminates on any report, including a corrupt one.
	 */
	import type { BlockedNode } from '$lib/reconcile';
	import { settle } from '$lib/design/motion';
	import ResourceRow from '$lib/components/ResourceRow.svelte';
	import Self from '$lib/components/BlockedTree.svelte';

	interface Props {
		nodes: BlockedNode[];
		/** The resource the loop is working on this instant, if it is in here. */
		currentResource?: string;
		/**
		 * True for every level but the first, which is what draws the rail.
		 *
		 * A flag rather than a `.tree .tree` descendant rule: the recursion happens through a
		 * component boundary, so Svelte's scoped-CSS analysis cannot see that the inner `.tree`
		 * exists and prunes the selector as unused. Saying it explicitly is cheaper than reaching
		 * for `:global`, which would leak the rule onto any other `.tree` on the page.
		 */
		nested?: boolean;
	}

	let { nodes, currentResource, nested = false }: Props = $props();
</script>

<ul class="tree" class:nested>
	{#each nodes as node, index (node.resource.name)}
		<li in:settle={{ index, count: nodes.length, y: 6 }}>
			<ResourceRow resource={node.resource} current={node.resource.name === currentResource} />
			{#if node.waiting.length > 0}
				<Self nodes={node.waiting} {currentResource} nested />
			{/if}
		</li>
	{/each}
</ul>

<style>
	.tree {
		display: grid;
		gap: var(--space-1);
		margin: 0;
		padding: 0 0 0 var(--space-5);
		list-style: none;
	}

	/* Every level but the first hangs off a rail, so the nesting is legible without counting
	   indents. The first level's rail is drawn by the panel that owns the cause. */
	.tree.nested {
		margin-top: var(--space-1);
		border-left: 1px solid var(--line);
	}

	li {
		display: grid;
		gap: var(--space-1);
		position: relative;
	}

	/* The tick joining a row to its parent's rail — drawn only where there is a rail to join.
	   The first level hangs off the fault card itself, so a tick there would point at nothing. */
	.tree.nested > li::before {
		content: '';
		position: absolute;
		left: calc(-1 * var(--space-5));
		top: 14px;
		width: var(--space-4);
		height: 1px;
		background: var(--line);
	}
</style>
