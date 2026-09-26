// Spotikey Bridge - runs inside music.youtube.com.
// YouTube Music is a Polymer app whose player bar exposes its state as element properties
// (shuffleOn, repeatMode, likeButtonRenderer...) and ignores bare element.click(); its buttons want
// a full pointer sequence. So we drive the bar's own API where one exists and press buttons
// otherwise. The queue is already rendered in the DOM, so no panel open/close flicker.
(() => {
  if (window.__spotikeyBridge) return;
  window.__spotikeyBridge = true;

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const text = (el) => ((el && el.textContent) || '').replace(/\s+/g, ' ').trim();

  const bar = () => $('ytmusic-player-bar');
  const video = () => $('ytmusic-player video') || $('video');

  // The player bar ignores element.click(); it reacts to pointer/mouse events.
  function press(el) {
    if (!el) return;
    const r = el.getBoundingClientRect();
    const x = r.left + r.width / 2, y = r.top + r.height / 2;
    const o = { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0, buttons: 1, pointerId: 1, isPrimary: true, pointerType: 'mouse', view: window };
    el.dispatchEvent(new PointerEvent('pointerdown', o));
    el.dispatchEvent(new MouseEvent('mousedown', o));
    el.dispatchEvent(new PointerEvent('pointerup', { ...o, buttons: 0 }));
    el.dispatchEvent(new MouseEvent('mouseup', { ...o, buttons: 0 }));
    el.dispatchEvent(new MouseEvent('click', { ...o, buttons: 0 }));
  }

  // ---------- reading state ----------

  function nowPlaying() {
    const b = bar();
    if (!b) return null;
    const title = text($('.title', b));
    const byline = $('.byline', b);
    const artist = byline
      ? $$('a', byline).map(text).filter(Boolean).join(', ') || text(byline)
      : '';
    const img = $('img', b);
    return { title, artist, artUrl: img && img.src ? img.src : null, liked: liked() };
  }

  function liked() {
    const lr = bar() && bar().likeButtonRenderer;
    return lr && typeof lr.likeStatus === 'string' ? lr.likeStatus === 'LIKE' : null;
  }

  function isPlaying() {
    const v = video();
    return v ? !v.paused : null;
  }

  function shuffleState() {
    const b = bar();
    return b && typeof b.shuffleOn === 'boolean' ? (b.shuffleOn ? 'on' : 'off') : null;
  }

  // 'off' | 'context' | 'track' | null
  function repeatState() {
    const m = bar() && bar().repeatMode;
    return m === 'ALL' ? 'context' : m === 'ONE' ? 'track' : m === 'NONE' ? 'off' : null;
  }

  function volume() {
    const s = $('#volume-slider');
    if (s && Number.isFinite(Number(s.value))) return Number(s.value) / 100;
    const b = bar();
    return b && Number.isFinite(Number(b.volume)) ? Number(b.volume) / 100 : null;
  }

  function state(extra = {}) {
    const b = bar();
    return {
      type: 'state',
      site: 'ytmusic',
      track: nowPlaying(),
      isPlaying: isPlaying(),
      shuffle: shuffleState(),
      repeat: repeatState(),
      volume: volume(),
      found: {
        widget: !!b,
        like: !!likeButton(),
        shuffle: !!b && typeof b.onShuffleButtonClick === 'function',
        repeat: !!b && typeof b.onRepeatButtonClick === 'function',
        volume: !!$('#volume-slider'),
        queueButton: false,
        queuePanel: queueItems().length > 0,
      },
      ...extra,
    };
  }

  // ---------- queue ----------

  function queueItems() {
    return $$('ytmusic-player-queue-item').map((li) => ({
      li,
      title: text($('.song-title', li)),
      artist: text($('.byline', li)),
      artUrl: ($('img', li) || {}).src || null,
    })).filter((i) => i.title);
  }

  // The currently playing item carries `selected`; the ones after it are up next.
  function upcoming() {
    const items = queueItems();
    const current = items.findIndex((i) => i.li.hasAttribute('selected'));
    return current >= 0 ? items.slice(current + 1) : items;
  }

  function readQueue(limit) {
    return { items: upcoming().map(({ title, artist, artUrl }) => ({ title, artist, artUrl })).slice(0, limit), source: '' };
  }

  async function playQueueItem(index, title, artist) {
    const next = upcoming();
    const entry = next.find((e) => e.title === title && (!artist || e.artist === artist))
      || (Number.isInteger(index) && index >= 0 ? next[index] : null);
    if (!entry) throw new Error('That song is no longer in the queue');
    press($('#play-button', entry.li) || $('.song-title', entry.li) || entry.li);
    await sleep(500);
    return entry.title;
  }

  // ---------- actions ----------

  function likeButton() {
    const renderers = $$('ytmusic-player-bar ytmusic-like-button-renderer');
    const r = renderers.find((x) => x.offsetParent !== null) || renderers[0];
    if (!r) return null;
    return $$('button', r).find((b) => /like/i.test(b.getAttribute('aria-label') || '') && !/dislike/i.test(b.getAttribute('aria-label') || '')) || null;
  }

  async function toggleShuffle() {
    const b = bar();
    if (!b || typeof b.onShuffleButtonClick !== 'function') throw new Error('Shuffle is not available here');
    b.onShuffleButtonClick();
    await sleep(400);
    return shuffleState();
  }

  async function cycleRepeat() {
    const b = bar();
    if (!b || typeof b.onRepeatButtonClick !== 'function') throw new Error('Repeat is not available here');
    b.onRepeatButtonClick();
    await sleep(300);
    return repeatState();
  }

  async function like() {
    const b = likeButton();
    if (!b) throw new Error('Like button not found');
    if (liked() === true) return { liked: true, changed: false };
    press(b);
    await sleep(500);
    return { liked: liked() === true, changed: true };
  }

  async function setVolume(target) {
    target = Math.max(0, Math.min(1, Math.round(target * 100) / 100));
    const s = $('#volume-slider');
    if (s) {
      s.value = Math.round(target * 100);
      s.dispatchEvent(new Event('change', { bubbles: true }));
      await sleep(150);
      return volume();
    }
    const v = video();
    if (v) { v.volume = target; await sleep(100); return target; }
    throw new Error('Volume control not found');
  }

  // ---------- message handling ----------

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    (async () => {
      try {
        const limit = Number(msg.limit) > 0 ? Number(msg.limit) : 5;
        switch (msg.action) {
          case 'getState':
            return state();
          case 'getQueue':
            return state({ queue: readQueue(limit) });
          case 'shuffle':
            return state({ message: (await toggleShuffle()) === 'on' ? 'Shuffle on' : 'Shuffle off' });
          case 'like': {
            const r = await like();
            return state({ message: r.changed ? (r.liked ? 'Added to liked songs' : 'Could not like it (sign in to YouTube Music)') : 'Already liked' });
          }
          case 'repeat': {
            const r = await cycleRepeat();
            return state({ message: r === 'off' ? 'Repeat off' : r === 'track' ? 'Repeat one' : 'Repeat all' });
          }
          case 'playQueue': {
            const t = await playQueueItem(Number(msg.index), msg.title || '', msg.artist || '');
            return state({ message: 'Playing: ' + t });
          }
          case 'volumeUp':
          case 'volumeDown': {
            const cur = volume();
            if (cur == null) throw new Error('Volume control not found');
            const step = Number(msg.step) > 0 ? Number(msg.step) : 0.1;
            const v = await setVolume(cur + (msg.action === 'volumeUp' ? step : -step));
            return state({ message: `Volume ${Math.round((v ?? cur) * 100)}%` });
          }
          default:
            throw new Error('Unknown action ' + msg.action);
        }
      } catch (e) {
        return state({ error: e.message });
      }
    })().then(sendResponse);
    return true; // async response
  });

  // Push track changes to the tray app (debounced).
  let lastKey = '';
  let pushTimer = null;
  const observer = new MutationObserver(() => {
    clearTimeout(pushTimer);
    pushTimer = setTimeout(() => {
      const np = nowPlaying();
      const key = np ? np.title + '|' + np.artist : '';
      if (key && key !== lastKey) {
        lastKey = key;
        try { chrome.runtime.sendMessage(state()); } catch { /* extension reloaded */ }
      }
    }, 300);
  });

  const start = () => {
    if (bar()) observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    else setTimeout(start, 500);
  };
  start();
})();
