import { LitElement, html, css } from './vendor/lit-all.min.js';
import './frame-tile.js';

// The call-mode video grid: 1-5 remote tiles auto-laid-out for the 1280x800 landscape
// panel, plus a mirrored self-view PiP. Tapping a tile promotes it to fullscreen
// (dispatches `promote`); tapping again clears it.
export class FrameGrid extends LitElement {
  static properties = {
    participants: { attribute: false },   // [{identity, name, track, muted, quality}]
    selfTrack: { attribute: false },
    largeId: {},
  };

  static styles = css`
    :host { position: absolute; inset: 0; display: block; background: #000; }
    .grid {
      position: relative; width: 100%; height: 100%;
      display: grid; gap: 4px; box-sizing: border-box; padding: 4px;
    }
    .grid[data-count="1"] { grid-template-columns: 1fr; }
    .grid[data-count="2"] { grid-template-columns: 1fr 1fr; }
    .grid[data-count="3"] { grid-template-columns: repeat(3, 1fr); }
    .grid[data-count="4"] { grid-template-columns: 1fr 1fr; grid-template-rows: 1fr 1fr; }
    .grid[data-count="5"] { grid-template-columns: repeat(6, 1fr); grid-template-rows: 1fr 1fr; }
    .grid[data-count="5"] > frame-tile:nth-child(1) { grid-column: 1 / 3; }
    .grid[data-count="5"] > frame-tile:nth-child(2) { grid-column: 3 / 5; }
    .grid[data-count="5"] > frame-tile:nth-child(3) { grid-column: 5 / 7; }
    .grid[data-count="5"] > frame-tile:nth-child(4) { grid-column: 2 / 4; grid-row: 2; }
    .grid[data-count="5"] > frame-tile:nth-child(5) { grid-column: 4 / 6; grid-row: 2; }

    /* Tap-to-promote: the chosen tile overlays the whole grid. */
    .grid > frame-tile[large] { position: absolute; inset: 4px; z-index: 5; }

    .self {
      position: absolute; right: 16px; bottom: 16px;
      width: 230px; height: 144px; z-index: 8;
      border-radius: 10px; overflow: hidden; box-shadow: 0 3px 12px rgba(0, 0, 0, .6);
    }
    .self frame-tile { width: 100%; height: 100%; }

    .empty {
      position: absolute; inset: 0; display: flex; align-items: center; justify-content: center;
      color: #555; font: 500 22px system-ui, sans-serif;
    }
  `;

  _onClick(e) {
    const tile = e.composedPath().find(
      (el) => el.tagName === 'FRAME-TILE' && el.classList.contains('remote')
    );
    if (!tile) return;
    this.dispatchEvent(new CustomEvent('promote', {
      detail: { id: tile.dataset.id }, bubbles: true, composed: true,
    }));
  }

  render() {
    const ps = this.participants || [];
    return html`
      <div class="grid" data-count=${ps.length} @click=${this._onClick}>
        ${ps.length === 0 ? html`<div class="empty">Waiting for others…</div>` : ''}
        ${ps.map((p) => html`
          <frame-tile
            class="remote"
            data-id=${p.identity}
            name=${p.name || p.identity}
            .track=${p.track}
            ?large=${p.identity === this.largeId}
            ?muted=${p.muted}
            quality=${p.quality || ''}
          ></frame-tile>
        `)}
      </div>
      ${this.selfTrack
        ? html`<div class="self"><frame-tile mirror name="You" .track=${this.selfTrack}></frame-tile></div>`
        : ''}
    `;
  }
}
customElements.define('frame-grid', FrameGrid);
