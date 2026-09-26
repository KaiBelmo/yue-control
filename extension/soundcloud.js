// yuecontrol bridge - runs inside soundcloud.com.
// SoundCloud's web player renders no <audio> element (Web Audio) and ignores synthetic clicks for
// some controls, so: shuffle/repeat/like are driven through their buttons, the set's track list is
// used as the queue (items expose .trackItem.active for the current sound), and a queue row is played
// by first firing mouseover to reveal its play button, then clicking it. Volume is not exposed in a
// way a content script can drive, so the volume hotkeys report that.
(() => {
  if (window.__yuecontrolBridge) return;
  window.__yuecontrolBridge = true;

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const text = (el) => ((el && el.textContent) || '').replace(/\s+/g, ' ').trim();
  const label = (el) => (el && (el.getAttribute('title') || el.getAttribute('aria-label') || '')) || '';

  const player = () => $('.playControls');

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

  function bgUrl(el) {
    if (!el || !el.style || !el.style.backgroundImage) return null;
    return el.style.backgroundImage.replace(/^url\(["']?/, '').replace(/["']?\)$/, '') || null;
  }

  // ---------- reading state ----------

  function nowPlaying() {
    const ms = navigator.mediaSession && navigator.mediaSession.metadata;
    let title = ms && ms.title;
    let artist = ms && ms.artist;
    let artUrl = ms && ms.artwork && ms.artwork.length ? ms.artwork[ms.artwork.length - 1].src : null;

    if (!title) {
      title = text($('.playbackSoundBadge__titleLink')).replace(/^Current track:\s*/i, '');
      const half = Math.floor(title.length / 2);
      if (title.length > 1 && title.slice(0, half) === title.slice(half)) title = title.slice(0, half);
    }
    if (!artist) artist = text($('.playbackSoundBadge__lightLink'));
    if (!artUrl) artUrl = bgUrl($('.playbackSoundBadge__avatar span'));

    return { title: title || '', artist: artist || '', artUrl, liked: liked() };
  }

  function likeButton() {
    return $('.playbackSoundBadge__like') || $('.playControls__soundBadge button.sc-button-like');
  }

  function liked() {
    const b = likeButton();
    if (!b) return null;
    return b.classList.contains('sc-button-selected') || /unlike/i.test(label(b));
  }

  function playButton() { return $('.playControls__play'); }

  function isPlaying() {
    const b = playButton();
    return b ? /pause/i.test(label(b)) : null;
  }

  function shuffleState() {
    const b = $('.shuffleControl');
    return b ? (b.classList.contains('m-shuffling') ? 'on' : 'off') : null;
  }

  // 'off' | 'context' | 'track' | null
  function repeatState() {
    const b = $('.repeatControl');
    if (!b) return null;
    if (b.classList.contains('m-one')) return 'track';
    if (b.classList.contains('m-all')) return 'context';
    return 'off';
  }

  function volume() {
    const w = $('.volume__sliderWrapper');
    const n = w && w.getAttribute('aria-valuenow');
    return n != null && n !== '' ? Number(n) : null;
  }

  function state(extra = {}) {
    return {
      type: 'state',
      site: 'soundcloud',
      track: nowPlaying(),
      isPlaying: isPlaying(),
      shuffle: shuffleState(),
      repeat: repeatState(),
      volume: volume(),
      found: {
        widget: !!player(),
        like: !!likeButton(),
        shuffle: !!$('.shuffleControl'),
        repeat: !!$('.repeatControl'),
        volume: false, // not drivable from a content script
        queueButton: !!$('.playbackSoundBadge__showQueue'),
        queuePanel: queueItems().length > 0,
      },
      ...extra,
    };
  }

  // ---------- queue (the set's track list) ----------

  function queueItems() {
    return $$('.trackList__item').map((li) => ({
      li,
      title: text($('.trackItem__trackTitle', li)),
      artist: text($('.trackItem__username', li)),
      artUrl: bgUrl($('.trackItem__image span', li)),
    })).filter((i) => i.title);
  }

  function upcoming() {
    const items = queueItems();
    const current = items.findIndex((i) => i.li.querySelector('.trackItem.active'));
    return current >= 0 ? items.slice(current + 1) : items;
  }

  function readQueue(limit) {
    return { items: upcoming().map(({ title, artist, artUrl }) => ({ title, artist, artUrl })).slice(0, limit), source: '' };
  }

  async function playQueueItem(index, title, artist) {
    const next = upcoming();
    const entry = next.find((e) => e.title === title && (!artist || e.artist === artist))
      || (Number.isInteger(index) && index >= 0 ? next[index] : null);
    if (!entry) throw new Error('That sound is no longer in the set');

    const item = entry.li.querySelector('.trackItem') || entry.li;
    item.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
    item.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
    await sleep(150);
    const btn = entry.li.querySelector('.sc-button-play');
    (btn || item).click();
    await sleep(500);
    return entry.title;
  }

  // ---------- actions ----------

  async function toggleShuffle() {
    const b = $('.shuffleControl');
    if (!b) throw new Error('Shuffle button not found');
    press(b);
    await sleep(400);
    return shuffleState();
  }

  async function cycleRepeat() {
    const b = $('.repeatControl');
    if (!b) throw new Error('Repeat button not found');
    press(b);
    await sleep(400);
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
            return state({ message: r.changed ? (r.liked ? 'Liked' : 'Could not like it (sign in to SoundCloud)') : 'Already liked' });
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
          case 'volumeDown':
            throw new Error('Volume is not controllable on SoundCloud');
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
