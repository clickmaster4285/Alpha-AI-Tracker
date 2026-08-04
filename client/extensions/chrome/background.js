// Alpha AI Tracker — Browser Journey Extension
// Captures tab navigation, URL changes, tab switches, and sends via Native Messaging.
//
// 🟡 FIX 2026-07-28: Added chrome.alarms keepalive that survives service worker termination
// (setInterval dies when SW is killed; alarms are persisted at browser level).
// Added event buffering: when native messaging port is disconnected, events are buffered
// in chrome.storage.local and flushed on reconnect. Prevents data loss during the ~5-30s
// window between service worker restart and native port re-establishment.

const HOST_NAME = "com.alphai.tracker";
let nativePort = null;
let reconnectTimer = null;
const RECONNECT_DELAY_MS = 2000;
const FLUSH_INTERVAL_MS = 27000;
const ALARM_NAME = "alpha-ai-keepalive";

// ─── Browser Session Identity ───
let BROWSER_SESSION_ID = null;

async function getBrowserSessionId() {
  if (BROWSER_SESSION_ID) return BROWSER_SESSION_ID;
  const stored = await chrome.storage.local.get("browserSessionId");
  if (stored.browserSessionId) {
    BROWSER_SESSION_ID = stored.browserSessionId;
    return BROWSER_SESSION_ID;
  }
  BROWSER_SESSION_ID = crypto.randomUUID();
  await chrome.storage.local.set({ browserSessionId: BROWSER_SESSION_ID });
  return BROWSER_SESSION_ID;
}

// ─── Event Buffer ───
// When native port is down, buffer events in chrome.storage.local.
// Flush on reconnect or periodically via alarm.
const BUFFER_KEY = "eventBuffer";

async function bufferEvent(message) {
  try {
    const data = await chrome.storage.local.get(BUFFER_KEY);
    const buf = data[BUFFER_KEY] || [];
    buf.push(message);
    if (buf.length > 500) buf.splice(0, buf.length - 500);
    await chrome.storage.local.set({ [BUFFER_KEY]: buf });
  } catch (err) {
    console.error("[Alpha AI] Buffer error:", err);
  }
}

async function flushBuffer() {
  const data = await chrome.storage.local.get(BUFFER_KEY);
  const buf = data[BUFFER_KEY] || [];
  if (buf.length === 0 || !nativePort) return;

  const batch = buf.splice(0, 50);
  await chrome.storage.local.set({ [BUFFER_KEY]: buf });

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

// ─── Native Messaging Connection ───

function connectNative() {
  if (nativePort) return;

  try {
    nativePort = chrome.runtime.connectNative(HOST_NAME);

    nativePort.onMessage.addListener((msg) => {
      if (msg?.status === "ok") {
        console.debug("[Alpha AI] Acknowledged:", msg.tabId);
      }
    });

    nativePort.onDisconnect.addListener(() => {
      console.warn("[Alpha AI] Native host disconnected:", chrome.runtime.lastError?.message || "unknown");
      nativePort = null;
      scheduleReconnect();
    });

    console.log("[Alpha AI] Connected to native host:", HOST_NAME);

    // Send initial heartbeat so the tracker knows we're alive immediately
    // (not just on the first alarm cycle ~27s later). Small delay to let
    // the native host process finish initializing.
    setTimeout(() => {
      try {
        nativePort.postMessage({ action: "ping", timestamp: Date.now(), browser: "chrome" });
      } catch (_) { /* port will reconnect via alarm */ }
    }, 500);

    // Flush buffered events on fresh connection
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

// ─── Alarm-based keepalive (survives SW termination) ───
function setupKeepaliveAlarm() {
  chrome.alarms.create(ALARM_NAME, { periodInMinutes: 0.45 });
}

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === ALARM_NAME) {
    // Keep the SW alive via a chrome.storage API call (resets the 30s idle timer)
    chrome.storage.local.get("browserSessionId");
    // If port is dead, try reconnecting
    if (!nativePort) {
      connectNative();
    } else {
      // Send ping via native port
      try {
        nativePort.postMessage({ action: "ping", timestamp: Date.now(), browser: "chrome" });
      } catch (err) {
        nativePort = null;
        connectNative();
      }
    }
  }
});

// ─── Tab Event Handlers ───

async function sendToNative(action, tab) {
  if (!tab?.url || tab.url.startsWith("chrome://") || tab.url.startsWith("about:")) {
    return;
  }

  const sessionId = await getBrowserSessionId();
  const message = {
    action: action,
    tabId: tab.id,
    url: tab.url,
    title: tab.title || "",
    timestamp: Date.now(),
    windowId: tab.windowId,
    index: tab.index,
    browser: "chrome",
    browserSessionId: sessionId
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
    tabId: tabId,
    windowId: windowId,
    isWindowClosing: isWindowClosing,
    timestamp: Date.now(),
    browser: "chrome",
    browserSessionId: BROWSER_SESSION_ID || "unknown"
  };

  if (!nativePort) {
    bufferEvent(message);
    return;
  }

  try {
    nativePort.postMessage(message);
  } catch (err) {
    bufferEvent(message);
  }
}

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if ((changeInfo.url || changeInfo.title) && tab?.url?.startsWith("http")) {
    sendToNative("updated", tab);
  }
});

chrome.tabs.onActivated.addListener(async (activeInfo) => {
  try {
    const tab = await chrome.tabs.get(activeInfo.tabId);
    sendToNative("activated", tab);
  } catch (err) {
    if (!err.message?.includes("No tab with id")) {
      console.error("[Alpha AI] onActivated error:", err);
    }
  }
});

chrome.tabs.onCreated.addListener((tab) => {
  if (tab.url && tab.url !== "chrome://newtab/") {
    sendToNative("created", tab);
  }
});

chrome.tabs.onRemoved.addListener((tabId, removeInfo) => {
  postCloseMessage(tabId, removeInfo.windowId, removeInfo.isWindowClosing);
});

// ─── Initialization ───

function initialize() {
  connectNative();
  setupKeepaliveAlarm();
}

initialize();

chrome.runtime.onStartup?.addListener(() => {
  connectNative();
  setupKeepaliveAlarm();
});

chrome.runtime.onInstalled?.addListener(() => {
  setupKeepaliveAlarm();
});

console.log("[Alpha AI] Browser Journey extension loaded");
