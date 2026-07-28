// Alpha AI Tracker — Browser Journey Extension
// Captures tab navigation, URL changes, tab switches, and sends via Native Messaging.

const HOST_NAME = "com.alphai.tracker";
let nativePort = null;
let reconnectTimer = null;
const RECONNECT_DELAY_MS = 5000;

// ─── Browser Session Identity ───
// Generated once per extension lifecycle. Persisted via chrome.storage.local
// so it survives Manifest V3 service worker idle restarts.
// The tracker uses this as part of the tab cache key to detect browser restarts
// and avoid tab-ID collision (e.g., old tab 42 vs new tab 42 after restart).
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
  } catch (err) {
    console.error("[Alpha AI] Failed to connect:", err);
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (reconnectTimer) clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connectNative, RECONNECT_DELAY_MS);
}

// ─── Tab Event Handlers ───

async function sendToNative(action, tab) {
  if (!nativePort) return;

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

  try {
    nativePort.postMessage(message);
  } catch (err) {
    console.error("[Alpha AI] Failed to send:", err);
  }
}

function postCloseMessage(tabId, windowId, isWindowClosing) {
  if (!nativePort) return;
  nativePort.postMessage({
    action: "closed",
    tabId: tabId,
    windowId: windowId,
    isWindowClosing: isWindowClosing,
    timestamp: Date.now(),
    browser: "chrome",
    browserSessionId: BROWSER_SESSION_ID || "unknown"
  });
}

// Tab URL / title changed — only http/https pages
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if ((changeInfo.url || changeInfo.title) && tab?.url?.startsWith("http")) {
    sendToNative("updated", tab);
  }
});

// Active tab switched
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

// Tab created
chrome.tabs.onCreated.addListener((tab) => {
  if (tab.url && tab.url !== "chrome://newtab/") {
    sendToNative("created", tab);
  }
});

// Tab removed (closed)
chrome.tabs.onRemoved.addListener((tabId, removeInfo) => {
  postCloseMessage(tabId, removeInfo.windowId, removeInfo.isWindowClosing);
});

// ─── Initialization ───

connectNative();

// Reconnect on service worker wakeup (Manifest V3)
chrome.runtime.onStartup?.addListener(() => {
  connectNative();
});

// Periodic keepalive to prevent service worker idle shutdown (MV3)
setInterval(() => {
  if (nativePort) {
    nativePort.postMessage({ action: "ping", timestamp: Date.now(), browser: "chrome" });
  } else {
    connectNative();
  }
}, 25000);

console.log("[Alpha AI] Browser Journey extension loaded");
