// Spotikey Bridge - service worker.
// Keeps a WebSocket open to the tray app on 127.0.0.1 and relays commands to the music tab
// (Spotify, YouTube Music or YouTube).

const PORT = 47321;
const SITES = ['https://open.spotify.com/*', 'https://music.youtube.com/*', 'https://www.youtube.com/*'];
let ws = null;
let retryTimer = null;

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  try {
    ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
  } catch (e) {
    ws = null;
    scheduleRetry();
    return;
  }

  ws.onopen = () => send({ type: 'hello', version: chrome.runtime.getManifest().version });

  ws.onmessage = async (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === 'ping') { send({ type: 'pong' }); return; }
    if (msg.type === 'command') {
      const result = await runCommand(msg);
      send({ ...result, type: 'result', id: msg.id });
    }
  };

  ws.onclose = () => { ws = null; scheduleRetry(); };
  ws.onerror = () => { /* onclose follows */ };
}

function scheduleRetry() {
  clearTimeout(retryTimer);
  retryTimer = setTimeout(connect, 3000);
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

// The tab that is actually making noise wins, so the hotkeys follow whatever you are
// listening to when both sites are open.
async function findMusicTab() {
  const tabs = await chrome.tabs.query({ url: SITES });
  if (!tabs.length) return null;
  return tabs.find(t => t.audible) || tabs.find(t => t.active) || tabs[0];
}

async function runCommand(msg) {
  const tab = await findMusicTab();
  if (!tab) return { ok: false, error: 'No open.spotify.com or music.youtube.com tab is open' };
  try {
    const res = await chrome.tabs.sendMessage(tab.id, msg);
    if (!res) return { ok: false, error: 'No answer from the music tab (reload it)' };
    return { ok: !res.error, ...res };
  } catch (e) {
    return { ok: false, error: 'The music tab did not answer - reload it after installing the extension' };
  }
}

// Content script pushes track changes; forward them to the tray app.
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg && msg.type === 'state') send({ ...msg, tabId: sender.tab && sender.tab.id });
});

// Alarms survive service worker suspension and re-establish the connection.
chrome.alarms.create('reconnect', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(connect);
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
