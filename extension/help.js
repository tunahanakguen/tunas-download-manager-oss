// Hilfeseite bei fehlender Verbindung zur App: Zustand zeigen, neu verbinden, Schritte erklären.

for (const el of document.querySelectorAll("[data-i18n]"))
  el.textContent = chrome.i18n.getMessage(el.dataset.i18n) || el.dataset.i18n;
document.documentElement.lang = chrome.i18n.getUILanguage();

const state = document.getElementById("state");
const error = document.getElementById("error");
const retry = document.getElementById("retry");

function show(status) {
  state.textContent = chrome.i18n.getMessage(status.connected ? "helpConnected" : "helpNotConnected");
  state.className = status.connected ? "ok" : "bad";
  error.textContent = !status.connected && status.error ? status.error : "";
}

chrome.runtime.sendMessage({ type: "tunas-status" }).then(show);

retry.addEventListener("click", async () => {
  retry.disabled = true;
  state.textContent = chrome.i18n.getMessage("helpChecking");
  state.className = "";
  error.textContent = "";
  show(await chrome.runtime.sendMessage({ type: "tunas-reconnect" }));
  retry.disabled = false;
});
