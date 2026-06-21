import { LitElement, html, css } from './vendor/lit-all.min.js';

// A single video tile. The <video> is attached imperatively from a LiveKit track
// (the SDK is imperative), driven declaratively by the `.track` property.
export class FrameTile extends LitElement {
  static properties = {
    name: {},
    track: { attribute: false },
    large: { type: Boolean, reflect: true },
    muted: { type: Boolean, reflect: true },
    quality: {},
    mirror: { type: Boolean, reflect: true },
  };

  static styles = css`
    :host { display: block; position: relative; overflow: hidden; background: #141414; }
    .slot { width: 100%; height: 100%; }
    .slot video { width: 100%; height: 100%; object-fit: cover; display: block; }
    :host([mirror]) .slot video { transform: scaleX(-1); }
    .label {
      position: absolute; left: 8px; bottom: 8px; max-width: 72%;
      padding: 3px 10px; border-radius: 6px;
      background: rgba(0, 0, 0, .55); color: #fff;
      font: 600 16px system-ui, sans-serif;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .badges { position: absolute; top: 8px; right: 8px; display: flex; gap: 7px; align-items: center; }
    .mic { font-size: 18px; filter: drop-shadow(0 1px 1px #000); }
    .q { width: 11px; height: 11px; border-radius: 50%; box-shadow: 0 0 0 2px rgba(0, 0, 0, .4); }
    .q.poor { background: #e0a000; }
    .q.lost { background: #e04444; }
  `;

  updated(changed) {
    if (changed.has('track')) this._attach();
  }

  _attach() {
    const slot = this.renderRoot.querySelector('.slot');
    if (!slot) return;
    slot.replaceChildren();
    if (this.track) {
      const v = this.track.attach();
      v.autoplay = true;
      v.playsInline = true;
      if (this.mirror) v.muted = true;   // never play your own audio back
      slot.appendChild(v);
    }
  }

  render() {
    const badQ = this.quality === 'poor' || this.quality === 'lost';
    return html`
      <div class="slot"></div>
      ${this.name ? html`<div class="label">${this.name}</div>` : ''}
      <div class="badges">
        ${this.muted ? html`<span class="mic">🔇</span>` : ''}
        ${badQ ? html`<span class="q ${this.quality}"></span>` : ''}
      </div>
    `;
  }
}
customElements.define('frame-tile', FrameTile);
