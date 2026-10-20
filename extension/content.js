// TUNAS Download Manager – Video-Symbol.
// Läuft in jedem Frame. Erscheint oben rechts an sichtbaren Videos (beim Abspielen und Überfahren),
// ein Klick schickt die Seite an die App, die dort die Qualitäten anbietet.
(() => {
  if (window.__tunasVideoButton) return;
  window.__tunasVideoButton = true;

  const MIN_WIDTH = 200;
  const MIN_HEIGHT = 112;
  const VISIBLE_MS = 4000;
  // Logo direkt im Skript: Seiten wie YouTube melden sonst „Unsafe attempt to load URL chrome-extension://…“.
  const LOGO = "data:image/png;base64,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";

  let host = null;
  let video = null;
  let hideTimer = 0;
  let hovering = false;
  let enabled = false; // von der App: Video-Symbol an/aus
  let askedAt = 0;     // „aus“ nicht für immer merken: App kann später starten oder umschalten

  function ask(message) {
    return new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage(message, (reply) => resolve(chrome.runtime.lastError ? null : reply));
      } catch {
        resolve(null); // Erweiterung neu geladen: dieses Skript ist verwaist.
      }
    });
  }

  // Jeden Hinweis nur einmal pro Seite ins App-Protokoll.
  const reported = new Set();
  function diag(text) {
    if (reported.has(text)) return;
    reported.add(text);
    ask({ type: "tunas-diag", text });
  }
  const size = (v) => { const r = v.getBoundingClientRect(); return `${Math.round(r.width)}×${Math.round(r.height)}`; };

  function createHost() {
    host = document.createElement("tunas-video-button");
    host.style.cssText = "all:initial;position:fixed;z-index:2147483647;display:none;top:0;left:0;";
    const shadow = host.attachShadow({ mode: "closed" });
    shadow.innerHTML = `
      <style>
        button{all:initial;display:flex;align-items:center;gap:8px;cursor:pointer;
          font:600 13px/1 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;
          color:#000;background:rgba(255,255,255,.94);padding:8px 13px 8px 9px;border-radius:999px;
          box-shadow:0 4px 18px rgba(0,0,0,.35);transition:transform .12s ease,background .12s ease;}
        button:hover{background:#fff;transform:translateY(-1px);}
        button:active{transform:scale(.97);}
        .logo{width:20px;height:20px;border-radius:50%;background:#0A3F82 url("${LOGO}") center/cover;}
        .x{all:initial;margin-left:4px;cursor:pointer;color:#fff;background:rgba(0,0,0,.55);border-radius:50%;
          width:20px;height:20px;display:flex;align-items:center;justify-content:center;font:600 12px/1 system-ui;}
        .wrap{display:flex;align-items:center;}
      </style>
      <div class="wrap">
        <button title="${chrome.i18n.getMessage("videoButtonTitle")}"><span class="logo"></span>${chrome.i18n.getMessage("videoButton")}</button>
        <span class="x" title="${chrome.i18n.getMessage("hide")}">✕</span>
      </div>`;
    shadow.querySelector("button").addEventListener("click", onClick);
    shadow.querySelector(".x").addEventListener("click", (e) => {
      e.stopPropagation();
      if (video) video.__tunasHidden = true;
      hide();
    });
    host.addEventListener("mouseenter", () => { hovering = true; clearTimeout(hideTimer); });
    host.addEventListener("mouseleave", () => { hovering = false; scheduleHide(); });
    attach();
  }

  // Im Vollbild ist nur das Vollbild-Element sichtbar: dorthin umhängen.
  function attach() {
    const parent = document.fullscreenElement || document.documentElement;
    if (host && host.parentNode !== parent) parent.appendChild(host);
  }

  function usable(v) {
    if (!v || !v.isConnected || v.__tunasHidden) return false;
    const r = v.getBoundingClientRect();
    return r.width >= MIN_WIDTH && r.height >= MIN_HEIGHT && r.bottom > 0 && r.right > 0
      && r.top < innerHeight && r.left < innerWidth && getComputedStyle(v).visibility !== "hidden";
  }

  function place() {
    if (!host || !video) return;
    if (!usable(video)) { hide(); return; }
    const r = video.getBoundingClientRect();
    const top = Math.max(r.top, 0) + 12;
    const right = Math.min(r.right, innerWidth) - 12;
    host.style.top = `${top}px`;
    host.style.left = `${Math.max(right - host.offsetWidth, 4)}px`;
  }

  async function show(v) {
    if (!usable(v)) {
      diag(`Video nicht nutzbar (${size(v)})`);
      return;
    }
    if (!enabled && Date.now() - askedAt > 10000) {
      askedAt = Date.now();
      enabled = Boolean((await ask({ type: "tunas-video-config" }))?.videoButton);
    }
    if (!enabled) return;
    if (!host) createHost();
    video = v;
    attach();
    host.style.display = "block";
    place();
    scheduleHide();
    diag(`Symbol gezeigt (${size(v)})`);
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => { if (!hovering) hide(); }, VISIBLE_MS);
  }

  function hide() {
    if (host) host.style.display = "none";
  }

  async function onClick(e) {
    e.preventDefault();
    e.stopPropagation();
    const src = video?.currentSrc || video?.src || "";
    await ask({
      type: "tunas-video",
      url: location.href,
      title: document.title,
      // blob:-Quellen (YouTube, MSE) sind für die App nutzlos; dann zählt die Seiten-URL.
      src: /^https?:/i.test(src) ? src : null,
    });
    hide();
  }

  // Video unter dem Mauszeiger finden (Steuerleisten der Player liegen oft über dem <video>).
  let lastMove = 0;
  document.addEventListener("mousemove", (e) => {
    const now = Date.now();
    if (now - lastMove < 150) return;
    lastMove = now;
    for (const v of document.querySelectorAll("video")) {
      const r = v.getBoundingClientRect();
      if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) {
        if (v !== video || host?.style.display !== "block") show(v);
        else scheduleHide();
        return;
      }
    }
  }, { passive: true, capture: true });

  // Eingebettete Player (z. B. YouTube) schieben das <video> erst nach dem Start ins Bild: kurz danach erneut prüfen.
  const onStart = (e) => {
    if (!(e.target instanceof HTMLVideoElement)) return;
    show(e.target);
    setTimeout(() => show(e.target), 700);
  };
  document.addEventListener("play", onStart, true);
  document.addEventListener("playing", onStart, true);
  document.addEventListener("fullscreenchange", () => { attach(); place(); }, true);
  addEventListener("scroll", place, { passive: true, capture: true });
  addEventListener("resize", place, { passive: true });

  // Nur im obersten Frame: melden, dass das Skript läuft und wie viele Videos die Seite hat.
  if (window === top) {
    setTimeout(() => {
      const videos = document.querySelectorAll("video");
      if (videos.length) diag(`Skript aktiv, ${videos.length} Video(s): ${[...videos].map(size).join(", ")}`);
    }, 5000);
  }
})();
