/**
 * The four state transitions an operator can drive, in one place.
 *
 * The fleet screen and the device detail screen both offer adopt / block / unblock / forget,
 * and both need the same three things to happen around each call: the returned row folded
 * back into the list so the UI updates in the same frame as the press, a toast carrying the
 * server's own sentence, and a boolean the caller can bind a button's `busy` to.
 *
 * The wording below is chosen to match what the server actually does, which is not always
 * what the button name suggests:
 *
 *  - **Block** closes the device's socket immediately (`OperatorEndpoints.BlockAsync` calls
 *    `RequestClose`), and its next handshake is answered `blocked`, which stops its product.
 *    That is a visible consequence in someone's living room, so it is confirmed.
 *  - **Unblock** returns the device to *pending*, not to adopted — "the operator blocked it;
 *    deciding to trust it again is a separate, deliberate press". The toast says so, because
 *    an operator who expects the device to come straight back will otherwise think it failed.
 *  - **Forget** deletes the row. The device re-registers as pending on its next reconnect,
 *    and its per-device settings are gone.
 */

import { api } from '$lib/api/client';
import { fleet } from './fleet.svelte';
import { toasts } from './toast.svelte';

function nameOf(deviceId: string): string {
	return fleet.find(deviceId)?.name || deviceId;
}

export async function adoptDevice(deviceId: string, name?: string): Promise<boolean> {
	try {
		const device = await api.adopt(deviceId, name);
		fleet.merge(device);
		toasts.ok(
			`Adopted ${device.name || device.deviceId}`,
			'It receives its identity, settings and tokens on its next connect.'
		);
		return true;
	} catch (cause) {
		toasts.error('Could not adopt that device', cause);
		return false;
	}
}

export async function blockDevice(deviceId: string): Promise<boolean> {
	const label = nameOf(deviceId);
	try {
		const device = await api.block(deviceId);
		fleet.merge(device);
		toasts.ok(`Blocked ${label}`, 'Its connection was closed and its product has stopped.');
		return true;
	} catch (cause) {
		toasts.error('Could not block that device', cause);
		return false;
	}
}

export async function unblockDevice(deviceId: string): Promise<boolean> {
	const label = nameOf(deviceId);
	try {
		const device = await api.unblock(deviceId);
		fleet.merge(device);
		toasts.ok(
			`${label} is pending again`,
			'Unblocking returns a device to the adoption queue. Adopt it to bring it back.'
		);
		return true;
	} catch (cause) {
		toasts.error('Could not unblock that device', cause);
		return false;
	}
}

export async function forgetDevice(deviceId: string): Promise<boolean> {
	const label = nameOf(deviceId);
	try {
		await api.forget(deviceId);
		fleet.drop(deviceId);
		toasts.ok(
			`Forgot ${label}`,
			'Its settings are gone. It reappears as pending the next time it connects.'
		);
		return true;
	} catch (cause) {
		toasts.error('Could not forget that device', cause);
		return false;
	}
}
