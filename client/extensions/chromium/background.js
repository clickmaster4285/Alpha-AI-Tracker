// Alpha AI Tracker — Browser Journey (Chromium engine)
// Generic WebExtensions background: uses chrome.* (MV3 service worker).
// Engine tag in payloads is always "chromium" — never a browser brand name.

const HOST_NAME = "com.alphai.tracker";
const ENGINE = "chromium";
const api = typeof chrome !== "undefined" ? chrome : globalThis.browser;

let nativePort = null;
let reconnectTimer = null;
const RECONNECT_DELAY_MS = 2000;
const ALARM_NAME = "alpha-ai-keepalive";
const BUFFER_KEY = "eventBuffer";

let BROWSER_SESSION_ID = null;

async function getBrowserSessionId() {
  if (BROWSER_SESSION_ID) return BROWSER_SESSION_ID;
  const stored = await api.storage.local.get("browserSessionId");
  if (stored.browserSessionId) {
    BROWSER_SESSION_ID = stored.browserSessionId;
    return BROWSER_SESSION_ID;
  }
  BROWSER_SESSION_ID = crypto.randomUUID();
  await api.storage.local.set({ browserSessionId: BROWSER_SESSION_ID });
  return BROWSER_SESSION_ID;
}

async function bufferEvent(message) {
  try {
    const data = await api.storage.local.get(BUFFER_KEY);
    const buf = data[BUFFER_KEY] || [];
    buf.push(message);
    if (buf.length > 500) buf.splice(0, buf.length - 500);
    await api.storage.local.set({ [BUFFER_KEY]: buf });
  } catch (err) {
    console.error("[Alpha AI] Buffer error:", err);
  }
}

async function flushBuffer() {
  const data = await api.storage.local.get(BUFFER_KEY);
  const buf = data[BUFFER_KEY] || [];
  if (buf.length === 0 || !nativePort) return;

  const batch = buf.splice(0, 50);
  await api.storage.local.set({ [BUFFER_KEY]: buf });

  for (const msg of batch) {
    try {
      nativePort.postMessage(msg);
    } catch (err) {
      console.error("[Alpha AI] Flush failed:", err);
      await bufferEvent(msg);
      break;
    }
  }

  if (buf.length > 0 && nativePort) {
    setTimeout(flushBuffer, 500);
  }
}

function connectNative() {
  if (nativePort) return;

  try {
    nativePort = api.runtime.connectNative(HOST_NAME);

    nativePort.onMessage.addListener((msg) => {
      if (msg?.status === "ok") {
        console.debug("[Alpha AI] Acknowledged:", msg.tabId);
      }
    });

    nativePort.onDisconnect.addListener(() => {
      console.warn("[Alpha AI] Native host disconnected:", api.runtime.lastError?.message || "unknown");
      nativePort = null;
      scheduleReconnect();
    });

    console.log("[Alpha AI] Connected to native host:", HOST_NAME);

    setTimeout(() => {
      try {
        nativePort.postMessage({ action: "ping", timestamp: Date.now(), browser: ENGINE });
      } catch (_) { /* alarm will reconnect */ }
    }, 500);

    setTimeout(flushBuffer, 200);
  } catch (err) {
    console.error("[Alpha AI] Failed to connect:", err);
    nativePort = null;
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (reconnectTimer) clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connectNative, RECONNECT_DELAY_MS);
}

function setupKeepaliveAlarm() {
  if (!api.alarms) return;
  api.alarms.create(ALARM_NAME, { periodInMinutes: 0.45 });
}

if (api.alarms?.onAlarm) {
  api.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name !== ALARM_NAME) return;
    api.storage.local.get("browserSessionId");
    if (!nativePort) {
      connectNative();
    } else {
      try {
        nativePort.postMessage({ action: "ping", timestamp: Date.now(), browser: ENGINE });
      } catch (_) {
        nativePort = null;
        connectNative();
      }
    }
  });
}

function isInternalUrl(url) {
  if (!url) return true;
  return url.startsWith("chrome://") ||
    url.startsWith("chrome-extension://") ||
    url.startsWith("edge://") ||
    url.startsWith("about:") ||
    url.startsWith("devtools:");
}

async function sendToNative(action, tab) {
  if (!tab?.url || isInternalUrl(tab.url)) return;

  const sessionId = await getBrowserSessionId();
  const message = {
    action,
    tabId: tab.id,
    url: tab.url,
    title: tab.title || "",
    timestamp: Date.now(),
    windowId: tab.windowId,
    index: tab.index,
    browser: ENGINE,
    browserSessionId: sessionId,
  };

  if (!nativePort) {
    await bufferEvent(message);
    return;
  }

  try {
    nativePort.postMessage(message);
  } catch (err) {
    console.error("[Alpha AI] Failed to send:", err);
    await bufferEvent(message);
  }
}

function postCloseMessage(tabId, windowId, isWindowClosing) {
  const message = {
    action: "closed",
    tabId,
    windowId,
    isWindowClosing,
    timestamp: Date.now(),
    browser: ENGINE,
    browserSessionId: BROWSER_SESSION_ID || "unknown",
  };

  if (!nativePort) {
    bufferEvent(message);
    return;
  }

  try {
    nativePort.postMessage(message);
  } catch (_) {
    bufferEvent(message);
  }
}

api.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if ((changeInfo.url || changeInfo.title) && tab?.url?.startsWith("http")) {
    sendToNative("updated", tab);
  }
});

api.tabs.onActivated.addListener(async (activeInfo) => {
  try {
    const tab = await api.tabs.get(activeInfo.tabId);
    sendToNative("activated", tab);
  } catch (err) {
    if (!err.message?.includes("No tab with id")) {
      console.error("[Alpha AI] onActivated error:", err);
    }
  }
});

api.tabs.onCreated.addListener((tab) => {
  if (tab.url && !isInternalUrl(tab.url)) {
    sendToNative("created", tab);
  }
});

api.tabs.onRemoved.addListener((tabId, removeInfo) => {
  postCloseMessage(tabId, removeInfo.windowId, removeInfo.isWindowClosing);
});

function initialize() {
  connectNative();
  setupKeepaliveAlarm();
}

initialize();

api.runtime.onStartup?.addListener(() => {
  connectNative();
  setupKeepaliveAlarm();
});

api.runtime.onInstalled?.addListener(() => {
  setupKeepaliveAlarm();
});

console.log("[Alpha AI] Browser Journey extension loaded (chromium)");
