import { LitElement, html, css } from './vendor/lit-all.min.js';

// One-time setup screen, shown when the frame starts without a token. Dispatches `token`.
// (Persistent storage to disk is handled by the host; here we just collect + validate.)
export class FrameSetup extends LitElement {
  static properties = { identity: {}, error: {} };

  static styles = css`
    :host {
      position: absolute; inset: 0; z-index: 3000;
      display: flex; align-items: center; justify-content: center;
      background: #0a0a0a; color: #ddd; font-family: system-ui, sans-serif;
    }
    .card { width: min(620px, 86vw); text-align: center; }
    h1 { font-size: 26px; font-weight: 700; margin: 0 0 8px; }
    p { color: #999; margin: 0 0 22px; font-size: 16px; line-height: 1.4; }
    .id { color: #4a90d9; font-weight: 600; }
    textarea {
      width: 100%; height: 130px; box-sizing: border-box; resize: none;
      background: #161616; color: #ddd; border: 1px solid #333; border-radius: 8px;
      padding: 12px; font: 13px monospace;
    }
    button {
      margin-top: 16px; padding: 12px 28px; font-size: 17px; font-weight: 600;
      background: #4a90d9; color: #fff; border: 0; border-radius: 8px;
    }
    .err { color: #e06666; margin-top: 12px; min-height: 20px; }
  `;

  _save() {
    const token = this.renderRoot.querySelector('textarea').value.trim();
    if (!token || token.split('.').length !== 3) {
      this.error = 'That doesn’t look like a valid token.';
      return;
    }
    this.dispatchEvent(new CustomEvent('token', {
      detail: { token }, bubbles: true, composed: true,
    }));
  }

  render() {
    return html`
      <div class="card">
        <h1>FrameLink setup</h1>
        <p>This frame (<span class="id">${this.identity || 'unconfigured'}</span>) needs a
          one-time access token. Paste it below to finish setup.</p>
        <textarea placeholder="Paste the device token here"></textarea>
        <div class="err">${this.error || ''}</div>
        <button @click=${this._save}>Save &amp; start</button>
      </div>
    `;
  }
}
customElements.define('frame-setup', FrameSetup);
