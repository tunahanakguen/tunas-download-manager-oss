// TUNAS Download Manager – Browser-Erweiterung (Chrome, Edge, Firefox).
// Texte in _locales (de, en); der Browser wählt nach seiner Sprache.
// Übernimmt Downloads mit bekannten Dateitypen und reicht sie über den Native-Messaging-Host
// (com.tunas.dm → TunasDM.Host.exe → Named Pipe) an die App weiter.

const HOST = "com.tunas.dm";
const PROTOCOL = 1;

let port = null;
let config = null;      // von der App: { enabled, extensions, excludedSites }
let lastError = null;
let enabled = true;     // Schalter über das Symbol in der Leiste

// ---------- Verbindung zur App ----------

function connect() {
  if (port) return port;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    lastError = String(e);
    port = null;
    updateBadge();
    scheduleRetry();
    return null;
  }
  const current = port;
  port.onMessage.addListener(onAppMessage);
  port.onDisconnect.addListener(() => {
    if (port !== current) return; // alte Verbindung nach „Neu verbinden“
    lastError = chrome.runtime.lastError?.message ?? t("disconnected");
    port = null;
    config = null;
    updateBadge();
    scheduleRetry();
  });
  port.postMessage({ type: "hello", v: PROTOCOL, browser: browserName(), extVersion: chrome.runtime.getManifest().version });
  return port;
}

// Wer vor der ersten Antwort der App fragt (z. B. das Video-Symbol beim Seitenstart), wartet hier.
let configWaiters = [];

function waitForConfig(ms) {
  if (config) return Promise.resolve(config);
  connect();
  return new Promise((resolve) => {
    configWaiters.push(resolve);
    setTimeout(() => resolve(config), ms);
  });
}

// Verbindung verwerfen und neu aufbauen; liefert die Einstellungen der App oder null.
async function reconnect() {
  const old = port;
  port = null;
  config = null;
  try { old?.disconnect(); } catch { }
  return waitForConfig(4000);
}

// Ohne Verbindung jede Minute neu versuchen (App beendet, Browser vor der App gestartet …).
const RETRY_ALARM = "tunas-reconnect";

function scheduleRetry() {
  if (enabled) chrome.alarms.create(RETRY_ALARM, { periodInMinutes: 1 });
}

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name !== RETRY_ALARM) return;
  if (!enabled || config) chrome.alarms.clear(RETRY_ALARM);
  else if (!port) connect();
});

function onAppMessage(message) {
  if (message.type === "config") {
    config = message;
    lastError = null;
    chrome.alarms.clear(RETRY_ALARM);
    configWaiters.forEach((resolve) => resolve(config));
    configWaiters = [];
  } else if (message.type === "error") {
    lastError = message.message;
  }
  updateBadge();
}

function send(message) {
  const p = connect();
  if (!p) return false;
  try {
    p.postMessage({ v: PROTOCOL, ...message });
    return true;
  } catch (e) {
    lastError = String(e);
    return false;
  }
}

function t(key) {
  return chrome.i18n.getMessage(key) || key;
}

function browserName() {
  if (navigator.userAgent.includes("Firefox/")) return "firefox";
  return navigator.userAgent.includes("Edg/") ? "edge" : "chrome";
}

// ---------- Downloads übernehmen ----------

function extensionOf(name) {
  const base = (name || "").split(/[\\/]/).pop();
  const dot = base.lastIndexOf(".");
  return dot > 0 ? base.slice(dot + 1).toLowerCase() : "";
}

function nameFromUrl(url) {
  try {
    return decodeURIComponent(new URL(url).pathname.split("/").pop() || "");
  } catch {
    return "";
  }
}

function globToRegExp(pattern) {
  const escaped = pattern.trim().toLowerCase().replace(/[.+?^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*");
  return new RegExp(`^${escaped}$`);
}

function isExcluded(url) {
  if (!config?.excludedSites?.length) return false;
  let host;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return true;
  }
  return config.excludedSites.some((site) => globToRegExp(site).test(host));
}

function shouldCapture(item, fileName) {
  if (!enabled || !config?.enabled) return false;       // App nicht verbunden: Browser lädt selbst
  if (item.incognito) return false;                       // privates Fenster bleibt privat
  if (item.byExtensionId) return false;                   // Downloads anderer Erweiterungen nicht anfassen
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return false;               // blob:, data:, file: kann die App nicht laden
  if (!config.extensions.includes(extensionOf(fileName))) return false;
  return !isExcluded(url);
}

async function cookiesFor(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

async function takeOver(item, fileName) {
  const url = item.finalUrl || item.url;
  // Erst abbrechen und aus der Browser-Liste entfernen, dann an die App geben.
  try {
    await chrome.downloads.cancel(item.id);
    await chrome.downloads.erase({ id: item.id });
  } catch {
    // Schon beendet; trotzdem übergeben.
  }
  const sent = send({
    type: "download",
    url,
    fileName,
    fileSize: item.fileSize > 0 ? item.fileSize : item.totalBytes > 0 ? item.totalBytes : null,
    mime: item.mime || null,
    referrer: item.referrer || null,
    cookies: await cookiesFor(url),
    userAgent: navigator.userAgent,
  });
  if (!sent) {
    // App nicht erreichbar: der Browser lädt doch selbst.
    chrome.downloads.download({ url, filename: fileName || undefined });
  }
}

if (chrome.downloads.onDeterminingFilename) {
  // Chrome/Edge: onDeterminingFilename kennt Dateiname, Typ und Größe – der richtige Moment für die Entscheidung.
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    const fileName = (item.filename || "").split(/[\\/]/).pop();
    if (shouldCapture(item, fileName)) {
      takeOver(item, fileName);
    }
    suggest();
  });
} else {
  // Firefox kennt onDeterminingFilename nicht; bei onCreated steht der Zielpfad (und damit der Name) schon fest.
  chrome.downloads.onCreated.addListener((item) => {
    const fileName = (item.filename || "").split(/[\\/]/).pop() || nameFromUrl(item.url);
    if (shouldCapture(item, fileName)) {
      takeOver(item, fileName);
    }
  });
}

// ---------- Kontextmenü ----------

function createMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: "tunas-link", title: t("menuLink"), contexts: ["link", "image", "video", "audio"] });
    chrome.contextMenus.create({ id: "tunas-all", title: t("menuAll"), contexts: ["page", "selection"] });
    chrome.contextMenus.create({ id: "tunas-grab", title: t("menuGrab"), contexts: ["page"] });
  });
}

chrome.runtime.onInstalled.addListener(createMenus);
chrome.runtime.onStartup.addListener(createMenus);

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === "tunas-link") {
    const url = info.linkUrl || info.srcUrl;
    if (!url || !DOWNLOAD_URL.test(url)) return;
    send({ type: "download", url, referrer: info.pageUrl || null, cookies: await cookiesFor(url), userAgent: navigator.userAgent });
  } else if (info.menuItemId === "tunas-all" && tab?.id != null) {
    const results = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: collectLinks,
      args: [Boolean(info.selectionText)],
    });
    const links = results?.[0]?.result ?? [];
    if (links.length) {
      send({ type: "links", links, referrer: tab.url, userAgent: navigator.userAgent });
    }
  } else if (info.menuItemId === "tunas-grab") {
    const url = info.pageUrl || tab?.url;
    if (url && /^https?:/i.test(url)) send({ type: "grab", url });
  }
});

const DOWNLOAD_URL = /^(https?|ftpe?s?):/i;

// Läuft in der Seite: alle Links sammeln (bei Auswahl nur die markierten).
function collectLinks(selectionOnly) {
  const selection = window.getSelection();
  const ranges = [];
  if (selectionOnly && selection) {
    for (let i = 0; i < selection.rangeCount; i++) ranges.push(selection.getRangeAt(i));
  }
  const inSelection = (node) => !ranges.length || ranges.some((r) => r.intersectsNode(node));
  const seen = new Set();
  const links = [];
  const add = (url, text, node) => {
    // Eigene Kopie des Musters: die Funktion läuft in der Seite und sieht DOWNLOAD_URL nicht.
    if (!url || seen.has(url) || !/^(https?|ftpe?s?):/i.test(url) || !inSelection(node)) return;
    seen.add(url);
    links.push({ url, text: (text || "").trim().slice(0, 200) || null });
  };
  for (const a of document.querySelectorAll("a[href]")) add(a.href, a.textContent, a);
  for (const m of document.querySelectorAll("img[src], video[src], audio[src], source[src]")) add(m.src, m.alt || m.title, m);
  return links.slice(0, 5000);
}

// ---------- Videos: Symbol über dem Video und Stream-Erkennung ----------

// HLS-/DASH-Listen pro Tab merken; YouTube braucht sie nicht (yt-dlp nimmt die Seiten-URL),
// andere Seiten liefern so die eigentliche Stream-Adresse.
const streams = new Map(); // tabId → [{ url, kind }]

chrome.webRequest.onBeforeRequest.addListener((details) => {
  if (details.tabId < 0) return;
  const match = /\.(m3u8|mpd)(\?|$)/i.exec(details.url);
  if (!match || /googlevideo\.com/i.test(details.url)) return;
  const list = streams.get(details.tabId) ?? [];
  if (!list.some((s) => s.url === details.url)) {
    list.push({ url: details.url, kind: match[1].toLowerCase() === "mpd" ? "dash" : "hls" });
    streams.set(details.tabId, list.slice(-20));
  }
}, { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "other"] });

chrome.webNavigation.onCommitted.addListener((details) => {
  if (details.frameId === 0) streams.delete(details.tabId);
});
chrome.tabs.onRemoved.addListener((tabId) => streams.delete(tabId));

// Hinweise ins App-Protokoll, damit sich ein fehlendes Video-Symbol ohne Entwicklertools klären lässt.
function diag(sender, text) {
  let host = "?";
  try { host = new URL(sender.url || sender.tab?.url).hostname; } catch { }
  send({ type: "diag", text: `${host}: ${text}` });
}

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (message?.type === "tunas-video-config") {
    waitForConfig(3000).then((c) => {
      const on = Boolean(enabled && c?.enabled && c?.videoButton);
      if (!on) diag(sender, `Video-Symbol aus (Leiste ${enabled ? "an" : "aus"}, App ${c ? (c.enabled ? "an" : "aus") : "nicht verbunden"}, Symbol ${c?.videoButton ? "an" : "aus"})`);
      reply({ videoButton: on });
    });
    return true; // Antwort kommt asynchron
  }
  // Hilfeseite: Zustand abfragen und neu verbinden.
  if (message?.type === "tunas-status") {
    reply({ enabled, connected: Boolean(config), error: lastError, browser: browserName(), version: chrome.runtime.getManifest().version });
    return;
  }
  if (message?.type === "tunas-reconnect") {
    enabled = true;
    chrome.storage.local.set({ enabled });
    reconnect().then((c) => {
      updateBadge();
      reply({ connected: Boolean(c), error: lastError });
    });
    return true;
  }
  if (message?.type === "tunas-diag") {
    diag(sender, message.text);
    return;
  }
  if (message?.type === "tunas-video") {
    const tabStreams = streams.get(sender.tab?.id) ?? [];
    const sent = send({
      type: "media",
      src: message.src,                      // direkte Quelle des angeklickten Videos, falls vorhanden
      url: message.url,                      // Seite oder eingebetteter Frame (z. B. youtube.com/embed/…)
      pageUrl: sender.tab?.url ?? null,
      // Titel des Tabs: im Player-Frame steht oft nur eine Nummer.
      title: sender.tab?.title || message.title || null,
      // Der Player-Frame lädt die Streams; CDNs prüfen oft genau diesen Referer.
      referrer: message.url || sender.tab?.url || null,
      userAgent: navigator.userAgent,
      streams: tabStreams.map((s) => s.url),
    });
    reply({ ok: sent });
  }
});

// ---------- Symbol in der Leiste: Übernahme ein/aus ----------

function updateBadge() {
  let text = "";
  let title = "TUNAS Download Manager";
  if (!enabled) {
    text = t("badgeOff");
    title += ` – ${t("titleOffClick")}`;
  } else if (!config) {
    text = "!";
    title += ` – ${t("titleNotConnected")}${lastError ? `: ${lastError}` : ""} (${t("titleRetryClick")})`;
  } else if (!config.enabled) {
    text = t("badgeOff");
    title += ` – ${t("titleOffInApp")}`;
  } else {
    title += ` – ${t("titleActive")}`;
  }
  chrome.action.setBadgeText({ text });
  chrome.action.setBadgeBackgroundColor({ color: text === "!" ? "#FF6464" : "#6C6C72" });
  chrome.action.setTitle({ title });
}

// Rotes „!“: erst neu verbinden; klappt es nicht, erklärt die Hilfeseite die nächsten Schritte.
// Sonst schaltet der Klick die Übernahme ein und aus.
chrome.action.onClicked.addListener(async () => {
  if (enabled && !config) {
    chrome.action.setBadgeText({ text: "…" });
    const c = await reconnect();
    updateBadge();
    if (!c) chrome.tabs.create({ url: chrome.runtime.getURL("help.html") });
    return;
  }
  enabled = !enabled;
  await chrome.storage.local.set({ enabled });
  if (enabled) connect();
  updateBadge();
});

// ---------- Start ----------

chrome.storage.local.get({ enabled: true }).then((state) => {
  enabled = state.enabled;
  connect();
  updateBadge();
});
