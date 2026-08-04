// Alpha AI Tracker — Browser Journey Extension (Firefox)
// Captures tab navigation, URL changes, tab switches, and sends via Native Messaging.
// Uses browser.* API for Firefox compatibility.

const HOST_NAME = "com.alphai.tracker";
let nativePort = null;
let reconnectTimer = null;
const RECONNECT_DELAY_MS = 5000;

// ─── Browser Session Identity ───
// Persisted via storage so it survives Firefox restarts.
// Tracker uses this to distinguish browser restarts from tab-ID reuse.
let BROWSER_SESSION_ID = null;

async function getBrowserSessionId() {
  if (BROWSER_SESSION_ID) return BROWSER_SESSION_ID;
  const stored = await browser.storage.local.get("browserSessionId");
  if (stored.browserSessionId) {
    BROWSER_SESSION_ID = stored.browserSessionId;
    return BROWSER_SESSION_ID;
  }
  BROWSER_SESSION_ID = crypto.randomUUID();
  await browser.storage.local.set({ browserSessionId: BROWSER_SESSION_ID });
  return BROWSER_SESSION_ID;
}

// ─── Native Messaging Connection ───
// Firefox uses browser.runtime.connectNative for native messaging.

function connectNative() {
  if (nativePort) return;

  try {
    nativePort = browser.runtime.connectNative(HOST_NAME);

    nativePort.onMessage.addListener((msg) => {
      if (msg?.status === "ok") {
        console.debug("[Alpha AI] Acknowledged:", msg.tabId);
      }
    });

    nativePort.onDisconnect.addListener(() => {
      console.warn("[Alpha AI] Native host disconnected");
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

  if (!tab?.url || tab.url.startsWith("about:") || tab.url.startsWith("moz-extension://")) {
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
    browser: "firefox",
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
    browser: "firefox",
    browserSessionId: BROWSER_SESSION_ID || "unknown"
  });
}

// Tab URL / title changed — only http/https pages
browser.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if ((changeInfo.url || changeInfo.title) && tab?.url?.startsWith("http")) {
    sendToNative("updated", tab);
  }
});

// Active tab switched
browser.tabs.onActivated.addListener(async (activeInfo) => {
  try {
    const tab = await browser.tabs.get(activeInfo.tabId);
    sendToNative("activated", tab);
  } catch (err) {
    if (!err.message?.includes("No tab with id")) {
      console.error("[Alpha AI] onActivated error:", err);
    }
  }
});

// Tab created
browser.tabs.onCreated.addListener((tab) => {
  if (tab.url && tab.url !== "about:newtab") {
    sendToNative("created", tab);
  }
});

// Tab removed (closed)
browser.tabs.onRemoved.addListener((tabId, removeInfo) => {
  postCloseMessage(tabId, removeInfo.windowId, removeInfo.isWindowClosing);
});

// ─── Initialization ───

connectNative();

// Keepalive ping for Firefox
setInterval(() => {
  if (nativePort) {
    nativePort.postMessage({ action: "ping", timestamp: Date.now(), browser: "firefox" });
  } else {
    connectNative();
  }
}, 25000);

console.log("[Alpha AI] Browser Journey extension (Firefox) loaded");
