/**
 * Transient messages.
 *
 * Every action in this app is a network call that can be refused, and the server writes its
 * own refusal sentences (`ApiError.Detail`: "Sentence fit to show an operator"). A toast is
 * where those land, verbatim. The GUI does not paraphrase a server error — if the sentence
 * reads badly, that is a finding to take back to the server, not a string to rewrite here.
 */

export type ToastTone = 'ok' | 'danger' | 'info';

export interface Toast {
	id: number;
	tone: ToastTone;
	title: string;
	detail?: string;
	/** Milliseconds on screen. Errors linger; confirmations do not. */
	ttl: number;
}

let nextId = 1;

class ToastState {
	items = $state<Toast[]>([]);

	#push(tone: ToastTone, title: string, detail: string | undefined, ttl: number) {
		const toast: Toast = { id: nextId++, tone, title, detail, ttl };
		this.items = [...this.items, toast];
		setTimeout(() => this.dismiss(toast.id), ttl);
		return toast.id;
	}

	/** A thing worked. Short, because the screen behind it already shows the outcome. */
	ok(title: string, detail?: string) {
		return this.#push('ok', title, detail, 3600);
	}

	/** A thing failed. Longer, because the operator has to read and decide. */
	fail(title: string, detail?: string) {
		return this.#push('danger', title, detail, 8000);
	}

	info(title: string, detail?: string) {
		return this.#push('info', title, detail, 5000);
	}

	/** Reports a caught error, preferring the server's own sentence over anything invented. */
	error(title: string, cause: unknown) {
		const detail =
			cause instanceof Error && cause.message ? cause.message : 'The Fleet Manager did not say why.';
		return this.fail(title, detail);
	}

	dismiss(id: number) {
		this.items = this.items.filter((toast) => toast.id !== id);
	}
}

export const toasts = new ToastState();
