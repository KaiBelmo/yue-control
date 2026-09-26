// Spotikey Bridge - runs inside open.spotify.com.
// Reads the player UI and clicks Spotify's own buttons on behalf of the tray app.
// Selectors are based on Spotify's data-testid attributes (stable across redesigns so far).
(() => {
  if (window.__spotikeyBridge) return;
  window.__spotikeyBridge = true;

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const text = (el) => ((el && el.textContent) || '').replace(/\s+/g, ' ').trim();
  const label = (el) => (el && el.getAttribute('aria-label')) || '';

  const SEL = {
    widget: '[data-testid="now-playing-widget"]',
    title: '[data-testid="context-item-info-title"]',
    artist: '[data-testid="context-item-info-artist"]',
    subtitles: '[data-testid="context-item-info-subtitles"]',
    cover: '[data-testid="cover-art-image"]',
    like: '[data-testid="now-playing-widget"] button[aria-checked]',
    controls: '[data-testid="player-controls"]',
    playPause: '[data-testid="control-button-playpause"]',
    repeat: '[data-testid="control-button-repeat"]',
    queueButton: '[data-testid="control-button-queue"]',
    volumeInput: '[data-testid="volume-bar"] input[type="range"]',
    volumeBar: '[data-testid="volume-bar"] [data-testid="progress-bar"]',
    queueLists: 'aside ul[role="treegrid"]',
  };

  // ---------- reading state ----------

  function nowPlaying() {
    const w = $(SEL.widget);
    if (!w) return null;
    const title = text($(SEL.title, w));
    const artists = $$(SEL.artist, w).map(text).filter(Boolean);
    const artist = artists.join(', ') || text($(SEL.subtitles, w));
    const cover = $(SEL.cover, w);
    const like = $('button[aria-checked]', w);
    return {
      title,
      artist,
      artUrl: cover ? cover.src : null,
      liked: like ? like.getAttribute('aria-checked') === 'true' : null,
    };
  }

  function shuffleButton() {
    return $$(SEL.controls + ' button').find((b) => /shuffle/i.test(label(b))) || null;
  }

  // 'off' | 'on' | 'smart' | null. Spotify's label names the NEXT state.
  function shuffleState() {
    const b = shuffleButton();
    if (!b) return null;
    const l = label(b);
    if (/^enable shuffle/i.test(l)) return 'off';
    if (/disable/i.test(l)) return 'smart';
    if (/smart/i.test(l)) return 'on';
    return b.getAttribute('aria-checked') === 'true' ? 'on' : 'off';
  }

  // 'off' | 'context' | 'track' | null
  function repeatState() {
    const b = $(SEL.repeat);
    if (!b) return null;
    const c = b.getAttribute('aria-checked');
    return c === 'true' ? 'context' : c === 'mixed' ? 'track' : 'off';
  }

  function isPlaying() {
    const b = $(SEL.playPause);
    return b ? /pause/i.test(label(b)) : null;
  }

  function volumeInput() { return $(SEL.volumeInput); }
  function volume() {
    const v = volumeInput();
    return v ? parseFloat(v.value) : null;
  }

  function state(extra = {}) {
    return {
      type: 'state',
      site: 'spotify',
      track: nowPlaying(),
      isPlaying: isPlaying(),
      shuffle: shuffleState(),
      repeat: repeatState(),
      volume: volume(),
      found: {
        widget: !!$(SEL.widget),
        like: !!$(SEL.like),
        shuffle: !!shuffleButton(),
        repeat: !!$(SEL.repeat),
        volume: !!volumeInput(),
        queueButton: !!$(SEL.queueButton),
        queuePanel: !!$(SEL.queueLists),
      },
      ...extra,
    };
  }

  // ---------- queue ----------

  function nextUpList() {
    const lists = $$(SEL.queueLists);
    if (lists.length === 0) return null;
    return lists.find((l) => /next/i.test(label(l))) || lists[lists.length - 1];
  }

  // Rows of the "Next up" list with their DOM elements.
  function queueEntries() {
    const nextUp = nextUpList();
    if (!nextUp) return null;
    return Array.from(nextUp.children)
      .map((li) => {
        const artistLinks = $$('a[href^="/artist/"]', li).map(text).filter(Boolean);
        const artist = artistLinks.join(', ');
        const lines = (li.innerText || '')
          .split('\n')
          .map((s) => s.trim())
          .filter((s) => s && s !== 'E' && !/^explicit$/i.test(s));
        const title = lines.find((l) => l !== artist && !artistLinks.includes(l)) || '';
        const img = $('img', li);
        return { li, title, artist, artUrl: img ? img.src : null };
      })
      .filter((i) => i.title);
  }

  function readQueuePanel() {
    const entries = queueEntries();
    if (!entries) return null;
    const nextUp = nextUpList();
    const aside = nextUp ? nextUp.closest('aside') : null;
    const heading = aside ? $$('h2', aside).map(text).find((t) => /^next/i.test(t)) || '' : '';
    const items = entries.map(({ title, artist, artUrl }) => ({ title, artist, artUrl }));
    return { items, source: heading };
  }

  // Opens the queue panel if it is closed. Returns true if we opened it (caller closes it).
  async function ensureQueuePanel() {
    if (readQueuePanel()) return false;
    const btn = $(SEL.queueButton);
    if (!btn) throw new Error('Queue button not found');
    btn.click();
    for (let i = 0; i < 40; i++) {
      await sleep(50);
      const p = readQueuePanel();
      if (p && p.items.length) break;
    }
    return true;
  }

  function closeQueuePanel() {
    const btn = $(SEL.queueButton);
    if (btn && btn.getAttribute('aria-pressed') === 'true') btn.click();
  }

  // Plays a song from the "Next up" list: match by title+artist, fall back to position.
  async function playQueueItem(index, title, artist) {
    const opened = await ensureQueuePanel();
    try {
      const entries = queueEntries() || [];
      if (!entries.length) throw new Error('Queue is empty');
      let entry = entries.find((e) => e.title === title && e.artist === artist);
      if (!entry && Number.isInteger(index) && index >= 0 && index < entries.length) entry = entries[index];
      if (!entry) throw new Error('That song is no longer in the queue');

      const play = $('[data-testid="play-button"]', entry.li);
      if (play) {
        play.click();
      } else {
        entry.li.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true }));
      }
      await sleep(400);
      cachedQueue = null;
      return entry.title;
    } finally {
      if (opened) closeQueuePanel();
    }
  }

  let cachedQueue = null; // { items, source, forTrack }
  let queueInFlight = null;

  function trackKey(np) { return np ? np.title + '|' + np.artist : ''; }

  async function getQueue(fresh, limit) {
    if (queueInFlight) return queueInFlight;
    queueInFlight = (async () => {
      try { return await getQueueInner(fresh, limit); }
      finally { queueInFlight = null; }
    })();
    return queueInFlight;
  }

  async function getQueueInner(fresh, limit) {
    const key = trackKey(nowPlaying());
    let panel = readQueuePanel();

    if (!panel) {
      // Panel closed. Reuse the last snapshot if the current song is in it: the
      // songs before it have played, so drop them. Avoids flashing the panel on every skip.
      if (!fresh && cachedQueue && cachedQueue.items.length) {
        const idx = cachedQueue.items.findIndex((i) => i.title + '|' + i.artist === key);
        if (idx >= 0) {
          cachedQueue = { ...cachedQueue, items: cachedQueue.items.slice(idx + 1), forTrack: key };
          return { ...cachedQueue, items: cachedQueue.items.slice(0, limit), cached: true };
        }
        if (cachedQueue.forTrack === key) {
          return { ...cachedQueue, items: cachedQueue.items.slice(0, limit), cached: true };
        }
      }

      const btn = $(SEL.queueButton);
      if (!btn) return { items: [], source: '', error: 'Queue button not found' };

      btn.click();
      for (let i = 0; i < 40; i++) {
        await sleep(50);
        panel = readQueuePanel();
        if (panel && panel.items.length) break;
      }
      // Close it again only if we opened it.
      const again = $(SEL.queueButton);
      if (again && again.getAttribute('aria-pressed') === 'true') again.click();

      if (!panel) return { items: [], source: '', error: 'Queue panel did not open' };
    }

    cachedQueue = { items: panel.items, source: panel.source, forTrack: key };
    return { ...cachedQueue, items: panel.items.slice(0, limit), cached: false };
  }

  // ---------- actions ----------

  async function toggleShuffle() {
    if (!shuffleButton()) throw new Error('Shuffle button not found');
    if (shuffleState() === 'off') {
      shuffleButton().click();
      await sleep(250);
      return shuffleState();
    }
    // on -> smart -> off: click until off (max 3)
    for (let i = 0; i < 3 && shuffleState() !== 'off'; i++) {
      const b = shuffleButton();
      if (!b) break;
      b.click();
      await sleep(300);
    }
    return shuffleState();
  }

  async function like() {
    const b = $(SEL.like);
    if (!b) throw new Error('Like button not found');
    if (b.getAttribute('aria-checked') === 'true') return { liked: true, changed: false };
    b.click();
    await sleep(350);
    const after = $(SEL.like);
    return { liked: !!after && after.getAttribute('aria-checked') === 'true', changed: true };
  }

  async function cycleRepeat() {
    const b = $(SEL.repeat);
    if (!b) throw new Error('Repeat button not found');
    b.click();
    await sleep(250);
    return repeatState();
  }

  async function setVolume(target) {
    const input = volumeInput();
    if (!input) throw new Error('Volume slider not found');
    target = Math.max(0, Math.min(1, Math.round(target * 10) / 10));

    // React-controlled range input: set through the native setter and fire input/change.
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    setter.call(input, String(target));
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await sleep(100);

    if (Math.abs((volume() ?? -1) - target) > 0.05) {
      // Fallback: click the bar at the right spot.
      const bar = $(SEL.volumeBar) || input.parentElement;
      const r = bar.getBoundingClientRect();
      const x = r.left + Math.max(1, Math.min(r.width - 1, r.width * target));
      const y = r.top + r.height / 2;
      const el = document.elementFromPoint(x, y) || bar;
      const opts = { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0, buttons: 1, pointerId: 1, isPrimary: true, pointerType: 'mouse' };
      el.dispatchEvent(new PointerEvent('pointerdown', opts));
      el.dispatchEvent(new MouseEvent('mousedown', opts));
      el.dispatchEvent(new PointerEvent('pointerup', { ...opts, buttons: 0 }));
      el.dispatchEvent(new MouseEvent('mouseup', { ...opts, buttons: 0 }));
      el.dispatchEvent(new MouseEvent('click', { ...opts, buttons: 0 }));
      await sleep(100);
    }
    return volume();
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
            return state({ queue: await getQueue(!!msg.fresh, limit) });
          case 'shuffle': {
            const s = await toggleShuffle();
            return state({ message: s === 'off' ? 'Shuffle off' : s === 'smart' ? 'Smart Shuffle on' : 'Shuffle on' });
          }
          case 'like': {
            const r = await like();
            return state({ message: r.changed ? (r.liked ? 'Added to Liked Songs' : 'Could not add to Liked Songs') : 'Already in Liked Songs' });
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
            if (cur == null) throw new Error('Volume slider not found');
            const step = Number(msg.step) > 0 ? Number(msg.step) : 0.1;
            const v = await setVolume(cur + (msg.action === 'volumeUp' ? step : -step));
            return state({ message: `Spotify volume ${Math.round((v ?? cur) * 100)}%` });
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
      const key = trackKey(nowPlaying());
      if (key && key !== lastKey) {
        lastKey = key;
        try { chrome.runtime.sendMessage(state()); } catch { /* extension reloaded */ }
      }
    }, 300);
  });
  observer.observe(document.body, { childList: true, subtree: true, characterData: true });
})();
