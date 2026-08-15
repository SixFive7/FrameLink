/**
 * FrameLink design system — motion primitives.
 *
 * version2.md §7.4 makes motion "a first-class part of the design" on both surfaces. In
 * practice that means motion is *specified* rather than improvised: every animation in the
 * app is one of the named primitives below, drawing its duration and curve from the tokens
 * in `tokens.css`. A component that hand-rolls a `fly({ y: 7, duration: 300 })` is a bug in
 * the same way a component that hand-rolls `#ffb35c` is.
 *
 * The vocabulary, and what each one means:
 *
 * | Primitive  | Reads as                        | Used for                                    |
 * | ---------- | ------------------------------- | ------------------------------------------- |
 * | `rise`     | something arriving and settling | cards, panels, page sections                |
 * | `settle`   | `rise`, one item after another  | lists — a fleet appearing, a device joining |
 * | `pop`      | a confirmation, with overshoot  | adopt succeeded, a value saved              |
 * | `swap`     | a change of context             | route transitions                           |
 * | `slip`     | a small lateral reveal          | inline editors, expanding rows              |
 * | `reorder`  | a list rearranging itself       | FLIP on the device list                     |
 *
 * Two rules hold everywhere:
 *
 *  1. **Travel is optional; presence is not.** Under `prefers-reduced-motion` every primitive
 *     drops its translation and scale and keeps a 1 ms fade, so an element still *appears* —
 *     it just appears where it belongs instead of sliding there. Nothing is ever hidden from
 *     a reduced-motion user because an animation was skipped.
 *  2. **Nothing loops that the eye must track.** Ambient loops (the background aurora, the
 *     pending pulse) run on `--dur-ambient` and stop entirely under reduced motion.
 */

import { cubicOut, expoOut, backOut } from 'svelte/easing';
import { prefersReducedMotion } from 'svelte/motion';
import { flip } from 'svelte/animate';
import type { TransitionConfig } from 'svelte/transition';
import type { FlipParams } from 'svelte/animate';

/**
 * Duration tokens, in milliseconds, mirroring the `--dur-*` custom properties.
 *
 * Duplicated here rather than read from the cascade because Svelte transitions need a number
 * before the element is in the document, and `getComputedStyle` on a detached node returns
 * nothing. The two lists are short and change together; `tokens.css` is the source of truth
 * for the reasoning behind each value.
 */
export const duration = {
	instant: 90,
	quick: 160,
	base: 260,
	slow: 420,
	grand: 720
} as const;

/** Stagger step between neighbouring list items, in milliseconds. */
export const staggerStep = 34;

/** Total stagger budget. Beyond this the step shrinks so a large fleet never crawls in. */
const staggerBudget = 420;

/**
 * Delay for the item at `index` of a list of `count`.
 *
 * The step compresses once the list is long enough that a fixed 34 ms would run past the
 * budget, so eight devices and eighty devices both finish arriving in under half a second.
 * A staggered entrance is a flourish; making somebody wait for it is not.
 */
export function stagger(index: number, count = 1): number {
	if (prefersReducedMotion.current) return 0;
	const step = count > 1 ? Math.min(staggerStep, staggerBudget / (count - 1)) : staggerStep;
	return Math.round(index * step);
}

/** True when the user asked the platform for less movement. */
export function reduced(): boolean {
	return prefersReducedMotion.current;
}

interface RiseParams {
	/** Travel distance in px. Positive rises from below, negative descends from above. */
	y?: number;
	delay?: number;
	duration?: number;
	/** Slight scale-up on entry. Off by default; on for surfaces that own the screen. */
	scale?: number;
}

/**
 * `rise` — the default entrance. Fades in while travelling a short distance upward, on the
 * expo-out curve, so it starts fast and lands softly. This is what "a card appeared" looks
 * like everywhere in FrameLink.
 */
export function rise(node: Element, params: RiseParams = {}): TransitionConfig {
	const soft = prefersReducedMotion.current;
	const y = soft ? 0 : (params.y ?? 10);
	const scale = soft ? 0 : (params.scale ?? 0);
	const existing = getTransform(node);

	return {
		delay: soft ? 0 : (params.delay ?? 0),
		duration: soft ? 1 : (params.duration ?? duration.base),
		easing: expoOut,
		css: (t, u) =>
			`opacity:${t};transform:${existing} translate3d(0,${u * y}px,0) scale(${1 - u * scale})`
	};
}

/**
 * `settle` — `rise` with the stagger already applied. Give it the item's index and the list
 * length and the whole list arrives as one gesture rather than as N independent events.
 */
export function settle(
	node: Element,
	params: RiseParams & { index?: number; count?: number } = {}
): TransitionConfig {
	return rise(node, { ...params, delay: (params.delay ?? 0) + stagger(params.index ?? 0, params.count) });
}

/**
 * `pop` — the confirmation. Overshoots by roughly 5% on the back-out curve, which is what
 * makes a completed adoption feel like a *press* rather than a repaint.
 *
 * Reserved for outcomes. A menu that pops develops a tic; an adopt button that pops reads as
 * a machine agreeing with you.
 */
export function pop(node: Element, params: { delay?: number; duration?: number } = {}): TransitionConfig {
	const soft = prefersReducedMotion.current;
	const existing = getTransform(node);

	return {
		delay: params.delay ?? 0,
		duration: soft ? 1 : (params.duration ?? duration.base),
		easing: backOut,
		css: (t, u) => `opacity:${Math.min(1, t * 1.6)};transform:${existing} scale(${soft ? 1 : 1 - u * 0.12})`
	};
}

/**
 * `swap` — a change of context. Used by the route transition: the outgoing screen drops and
 * fades on the accelerating curve, the incoming one rises on the entrance curve.
 */
export function swap(
	node: Element,
	params: { direction?: 'in' | 'out'; duration?: number } = {}
): TransitionConfig {
	const soft = prefersReducedMotion.current;
	const outgoing = params.direction === 'out';
	const y = soft ? 0 : outgoing ? -8 : 14;
	const existing = getTransform(node);

	return {
		duration: soft ? 1 : (params.duration ?? (outgoing ? duration.quick : duration.slow)),
		easing: outgoing ? cubicOut : expoOut,
		css: (t, u) => `opacity:${t};transform:${existing} translate3d(0,${u * y}px,0)`
	};
}

/**
 * `slip` — a small lateral reveal for something opening in place: an inline name field, a
 * settings row turning editable. Travels sideways rather than vertically so it does not read
 * as "a new card arrived".
 */
export function slip(node: Element, params: { x?: number; duration?: number } = {}): TransitionConfig {
	const soft = prefersReducedMotion.current;
	const x = soft ? 0 : (params.x ?? -10);
	const existing = getTransform(node);

	return {
		duration: soft ? 1 : (params.duration ?? duration.quick),
		easing: expoOut,
		css: (t, u) => `opacity:${t};transform:${existing} translate3d(${u * x}px,0,0)`
	};
}

/**
 * `collapse` — height + opacity, for a region that folds away rather than departs. The one
 * primitive that animates layout, and it is deliberately the only one: everything else moves
 * on the compositor.
 */
export function collapse(node: Element, params: { duration?: number } = {}): TransitionConfig {
	const soft = prefersReducedMotion.current;
	const height = (node as HTMLElement).offsetHeight;
	const style = getComputedStyle(node);
	const paddingTop = parseFloat(style.paddingTop);
	const paddingBottom = parseFloat(style.paddingBottom);

	return {
		duration: soft ? 1 : (params.duration ?? duration.base),
		easing: cubicOut,
		css: (t) =>
			`overflow:hidden;opacity:${t};height:${t * height}px;` +
			`padding-top:${t * paddingTop}px;padding-bottom:${t * paddingBottom}px`
	};
}

/**
 * `reorder` — the FLIP animation for a keyed list. The device list reorders on every poll
 * (the server sorts by last-seen), and without this a frame checking in makes the whole
 * list jump. With it, rows visibly slide to their new position and the operator's eye keeps
 * hold of the row it was reading.
 */
export function reorder(node: Element, options: { from: DOMRect; to: DOMRect }, params: FlipParams = {}) {
	return flip(node as HTMLElement, options, {
		duration: prefersReducedMotion.current ? 1 : duration.base,
		easing: expoOut,
		...params
	});
}

/**
 * Preserves any transform already on the element — a hover lift, a CSS animation — so a
 * transition does not stomp it on the first frame.
 */
function getTransform(node: Element): string {
	const current = getComputedStyle(node).transform;
	return current === 'none' ? '' : current;
}
