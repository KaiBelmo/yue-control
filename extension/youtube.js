// Spotikey Bridge - runs inside www.youtube.com (regular YouTube, incl. watch pages with a playlist).
// The player is the standard YouTube player: #movie_player exposes get/setVolume and getLoopVideo;
// the "Shuffle playlist" and "Loop playlist" buttons carry their state in aria-label/aria-pressed.
// Track title/artist/artwork come from the Media Session metadata the player publishes.
(() => {
  if (window.__spotikeyBridge) return;
  window.__spotikeyBridge = true;

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const text = (el) => ((el && el.textContent) || '').replace(/\s+/g, ' ').trim();
  const label = (el) => (el && (el.getAttribute('aria-label') || el.getAttribute('title') || '')) || '';

  const player = () => $('#movie_player');
  const video = () => $('#movie_player video') || $('video');

  // YouTube's buttons need a real pointer sequence; element.click() is ignored.
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

  function visible(el) { return el && el.offsetParent !== null; }

  function findByLabel(re) {
    const candidates = $$('button, a');
    return candidates.find((b) => visible(b) && re.test(label(b))) || null;
  }

  // ---------- reading state ----------

  function nowPlaying() {
    const ms = navigator.mediaSession && navigator.mediaSession.metadata;
    const title = (ms && ms.title) || text($('.ytp-title-text')) || document.title.replace(/\s*-\s*YouTube\s*$/, '');
    const owner = $('ytd-video-owner-renderer #channel-name a') || $('#owner ytd-channel-name a');
    const artist = (ms && ms.artist) || (owner ? text(owner) : '');
    let artUrl = null;
    if (ms && ms.artwork && ms.artwork.length) artUrl = ms.artwork[ms.artwork.length - 1].src;
    if (!artUrl) {
      const sel = $('ytd-playlist-panel-video-renderer[selected] img');
      artUrl = sel ? sel.src : null;
    }
    return { title, artist, artUrl, liked: liked() };
  }

  function likeButton() {
    const b = $('#segmented-like-button button')
      || $('like-button-view-model button')
      || findLikeIn($$('.ytp-like-button'));
    if (b) return b;
    return findByLabel(/^like\b/i);
    function findLikeIn(list) {
      return list.find((x) => /like/i.test(label(x)) && !/dislike/i.test(label(x))) || null;
    }
  }

  function liked() {
    const b = likeButton();
    return b && b.hasAttribute('aria-pressed') ? b.getAttribute('aria-pressed') === 'true' : null;
  }

  function isPlaying() {
    const v = video();
    return v ? !v.paused : null;
  }

  function shuffleButton() {
    return findByLabel(/shuffle/i);
  }

  function shuffleState() {
    const b = shuffleButton();
    if (!b) return null;
    return b.getAttribute('aria-pressed') === 'true' ? 'on' : 'off';
  }

  function loopButton() {
    return findByLabel(/loop|repeat/i);
  }

  // The button label names the NEXT action, so the current state is one step behind it.
  // Verified cycle: "Loop playlist" (off) -> "Loop video" (all) -> "Turn off loop" (one) -> ...
  // 'off' | 'context' | 'track' | null
  function repeatState() {
    const l = label(loopButton()).toLowerCase();
    if (/turn off/.test(l)) return 'track';
    if (/loop video/.test(l)) return 'context';
    if (/loop playlist/.test(l)) return 'off';
    return null;
  }

  function volume() {
    const p = player();
    if (p && typeof p.getVolume === 'function') {
      try { return p.getVolume() / 100; } catch { /* fall through */ }
    }
    const v = video();
    return v ? v.volume : null;
  }

  function state(extra = {}) {
    return {
      type: 'state',
      track: nowPlaying(),
      isPlaying: isPlaying(),
      shuffle: shuffleState(),
      repeat: repeatState(),
      volume: volume(),
      found: {
        widget: !!player(),
        like: !!likeButton(),
        shuffle: !!shuffleButton(),
        repeat: !!loopButton(),
        volume: volume() != null,
        queueButton: !!$('.ytp-playlist-menu-button'),
        queuePanel: queueItems().length > 0,
      },
      ...extra,
    };
  }

  // ---------- queue (the playlist panel) ----------

  function queueItems() {
    return $$('ytd-playlist-panel-video-renderer').map((li) => ({
      li,
      title: text($('#video-title', li)),
      artist: text($('#byline', li)),
      artUrl: ($('img', li) || {}).src || null,
    })).filter((i) => i.title);
  }

  function upcoming() {
    const items = queueItems();
    const current = items.findIndex((i) => i.li.hasAttribute('selected'));
    return current >= 0 ? items.slice(current + 1) : items;
  }

  function readQueue(limit) {
    return { items: upcoming().map(({ title, artist, artUrl }) => ({ title, artist, artUrl })).slice(0, limit), source: 'Up next' };
  }

  async function playQueueItem(index, title, artist) {
    const next = upcoming();
    const entry = next.find((e) => e.title === title && (!artist || e.artist === artist))
      || (Number.isInteger(index) && index >= 0 ? next[index] : null);
    if (!entry) throw new Error('That song is no longer in the playlist');
    press($('a#wc-endpoint', entry.li) || $('a#thumbnail', entry.li) || entry.li);
    await sleep(500);
    return entry.title;
  }

  // ---------- actions ----------

  async function toggleShuffle() {
    const b = shuffleButton();
    if (!b) throw new Error('Shuffle is not available (open a playlist)');
    press(b);
    await sleep(600);
    return shuffleState();
  }

  async function cycleRepeat() {
    const b = loopButton();
    if (!b) throw new Error('Loop is not available (open a playlist)');
    press(b);
    await sleep(600);
    return repeatState();
  }

  async function like() {
    const b = likeButton();
    if (!b) throw new Error('Like button not found');
    if (liked() === true) return { liked: true, changed: false };
    press(b);
    await sleep(600);
    return { liked: liked() === true, changed: true };
  }

  async function setVolume(target) {
    target = Math.max(0, Math.min(1, Math.round(target * 100) / 100));
    const p = player();
    if (p && typeof p.setVolume === 'function') {
      try { p.setVolume(Math.round(target * 100)); await sleep(120); return volume(); } catch { /* fall through */ }
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
            return state({ message: r.changed ? (r.liked ? 'Liked' : 'Could not like it (sign in to YouTube)') : 'Already liked' });
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
    if (player()) observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    else setTimeout(start, 500);
  };
  start();
})();
