<script lang="ts" module>
	/**
	 * The icon set, inline.
	 *
	 * Twenty-odd 24px stroked glyphs on a shared 2px weight, drawn as path data so they ship
	 * inside the JS bundle rather than as a font or a sprite request. No icon library: the
	 * set is small, it never grows by accident, and nothing about it can phone home.
	 *
	 * Every icon is decorative unless given a `label`, in which case it becomes an
	 * `img`-roled element with an accessible name.
	 */
	export const ICONS = {
		check: 'M20 6 9 17l-5-5',
		x: 'M18 6 6 18M6 6l12 12',
		plus: 'M12 5v14M5 12h14',
		minus: 'M5 12h14',
		shieldCheck: 'M12 3 4 6v6c0 5 3.4 8.4 8 9 4.6-.6 8-4 8-9V6l-8-3Z M9 12l2 2 4-4',
		ban: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z M5.6 5.6l12.8 12.8',
		frame: 'M4 4h16v16H4z M4 15l4-4 3 3 4-5 5 6',
		sliders: 'M4 6h10M18 6h2M4 12h4M12 12h8M4 18h10M18 18h2 M14 6a2 2 0 1 0 4 0 2 2 0 0 0-4 0Z M8 12a2 2 0 1 0 4 0 2 2 0 0 0-4 0Z M14 18a2 2 0 1 0 4 0 2 2 0 0 0-4 0Z',
		copy: 'M9 9h10v10H9z M15 9V5H5v10h4',
		sun: 'M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10Z M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
		moon: 'M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5Z',
		monitor: 'M3 4h18v12H3z M8 20h8M12 16v4',
		arrowLeft: 'M19 12H5M11 18l-6-6 6-6',
		chevronRight: 'M9 6l6 6-6 6',
		refresh: 'M21 12a9 9 0 1 1-2.6-6.4 M21 4v5h-5',
		trash: 'M4 7h16M10 11v6M14 11v6 M6 7l1 13h10l1-13 M9 7V4h6v3',
		alert: 'M12 3 2 20h20L12 3Z M12 9v5M12 17.5v.5',
		info: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z M12 11v5M12 8v.5',
		link: 'M10 13a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1.2 1.2 M14 11a4 4 0 0 0-5.7 0l-3 3A4 4 0 0 0 11 19.7l1.2-1.2',
		pencil: 'M4 20h4L19 9a2.8 2.8 0 0 0-4-4L4 16v4Z',
		logout: 'M15 4h4v16h-4 M10 8l-4 4 4 4M6 12h9',
		eye: 'M2 12s3.6-6 10-6 10 6 10 6-3.6 6-10 6-10-6-10-6Z M12 9.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5Z',
		eyeOff: 'M4 4l16 16 M10.6 10.6a2.5 2.5 0 0 0 3.4 3.4 M6.5 6.6C3.8 8.2 2 12 2 12s3.6 6 10 6c1.7 0 3.2-.4 4.5-1 M11 6.05C11.3 6 11.6 6 12 6c6.4 0 10 6 10 6s-.9 1.5-2.5 3',
		clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z M12 7v5l3.5 2',
		key: 'M14.5 4a5.5 5.5 0 1 0-4.3 8.9L4 19v3h3l1-1v-2h2v-2h2l1.2-1.2A5.5 5.5 0 0 0 14.5 4Z M16.5 8.5h.01'
	} as const;

	export type IconName = keyof typeof ICONS;
</script>

<script lang="ts">
	interface Props {
		name: IconName;
		size?: number | string;
		/** Turns the icon into a labelled image. Omit for decorative icons beside text. */
		label?: string;
		class?: string;
	}

	let { name, size = 18, label, class: className = '' }: Props = $props();
</script>

<svg
	class="icon {className}"
	width={size}
	height={size}
	viewBox="0 0 24 24"
	fill="none"
	stroke="currentColor"
	stroke-width="1.8"
	stroke-linecap="round"
	stroke-linejoin="round"
	role={label ? 'img' : 'presentation'}
	aria-label={label}
	aria-hidden={label ? undefined : 'true'}
>
	<path d={ICONS[name]} />
</svg>

<style>
	.icon {
		flex: none;
		display: block;
	}
</style>
