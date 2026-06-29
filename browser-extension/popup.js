const DEFAULT_SETTINGS = {
  silentDownload: false,
  autoHideOverlay: true,
  overlayPosition: "auto",
  experimentalAllSites: false,
  deepScanner: false,
  streamOverlay: false,
  browserCookies: false,
  cookieBrowser: "brave"
};

const silentDownloadInput = document.getElementById("silentDownload");
const autoHideOverlayInput = document.getElementById("autoHideOverlay");
const experimentalAllSitesInput = document.getElementById("experimentalAllSites");
const deepScannerInput = document.getElementById("deepScanner");
const deepScannerRow = document.getElementById("deepScannerRow");
const streamOverlayInput = document.getElementById("streamOverlay");
const browserCookiesInput = document.getElementById("browserCookies");
const cookieBrowserInput = document.getElementById("cookieBrowser");
const cookieBrowserRow = document.getElementById("cookieBrowserRow");
const overlayPositionInput = document.getElementById("overlayPosition");
const statusElement = document.getElementById("status");
const versionElement = document.getElementById("version");
const ibrahimLink = document.getElementById("ibrahimLink");
const sourceLink = document.getElementById("sourceLink");
const openAppButton = document.getElementById("openApp");
const openFolderButton = document.getElementById("openFolder");
const openDashboardButton = document.getElementById("openDashboard");

versionElement.textContent = chrome.runtime.getManifest().version;

function setStatus(text) {
  statusElement.textContent = text;
}

function render(settings, statusText = "") {
  silentDownloadInput.checked = Boolean(settings.silentDownload);
  autoHideOverlayInput.checked = Boolean(settings.autoHideOverlay);
  experimentalAllSitesInput.checked = Boolean(settings.experimentalAllSites);
  deepScannerInput.checked = Boolean(settings.experimentalAllSites && settings.deepScanner);
  deepScannerInput.disabled = !settings.experimentalAllSites;
  deepScannerRow.classList.toggle("is-disabled", !settings.experimentalAllSites);
  streamOverlayInput.checked = Boolean(settings.streamOverlay);
  browserCookiesInput.checked = Boolean(settings.browserCookies);
  cookieBrowserInput.disabled = !settings.browserCookies;
  cookieBrowserRow.classList.toggle("is-disabled", !settings.browserCookies);
  cookieBrowserInput.value = settings.cookieBrowser || DEFAULT_SETTINGS.cookieBrowser;
  overlayPositionInput.value = settings.overlayPosition || DEFAULT_SETTINGS.overlayPosition;
  setStatus(statusText);
}

chrome.storage.local.get(DEFAULT_SETTINGS, (settings) => {
  if (chrome.runtime.lastError) {
    setStatus(chrome.runtime.lastError.message);
    return;
  }

  render(settings);
});

silentDownloadInput.addEventListener("change", () => {
  saveSettings({ silentDownload: silentDownloadInput.checked });
});

autoHideOverlayInput.addEventListener("change", () => {
  saveSettings({ autoHideOverlay: autoHideOverlayInput.checked });
});

experimentalAllSitesInput.addEventListener("change", () => {
  saveSettings({ experimentalAllSites: experimentalAllSitesInput.checked });
});

deepScannerInput.addEventListener("change", () => {
  saveSettings({ deepScanner: deepScannerInput.checked });
});

streamOverlayInput.addEventListener("change", () => {
  saveSettings({ streamOverlay: streamOverlayInput.checked });
});

browserCookiesInput.addEventListener("change", () => {
  saveSettings({ browserCookies: browserCookiesInput.checked });
});

cookieBrowserInput.addEventListener("change", () => {
  saveSettings({ cookieBrowser: cookieBrowserInput.value });
});

overlayPositionInput.addEventListener("change", () => {
  saveSettings({ overlayPosition: overlayPositionInput.value });
});

function saveSettings(changes) {
  chrome.storage.local.set(changes, () => {
    if (chrome.runtime.lastError) {
      setStatus(chrome.runtime.lastError.message);
      return;
    }

    chrome.storage.local.get(DEFAULT_SETTINGS, (settings) => render(settings, "Saved"));
  });
}

function openTab(event, url) {
  event.preventDefault();
  chrome.tabs.create({ url });
}

function sendNativeCommand(action, statusText) {
  setStatus(statusText);

  chrome.runtime.sendMessage({ type: "dlp-native-command", action }, (response) => {
    if (chrome.runtime.lastError) {
      setStatus(chrome.runtime.lastError.message);
      return;
    }

    if (!response || response.ok === false) {
      setStatus(response?.message || "DLP request failed");
      return;
    }

    setStatus("Done");
  });
}

openAppButton.addEventListener("click", () => {
  sendNativeCommand("open_app", "Opening DLP");
});

openFolderButton.addEventListener("click", () => {
  sendNativeCommand("open_folder", "Opening folder");
});

openDashboardButton.addEventListener("click", () => {
  chrome.tabs.create({ url: chrome.runtime.getURL("dashboard.html#videos") });
});

ibrahimLink.addEventListener("click", (event) => openTab(event, "https://ibrhub.net"));
sourceLink.addEventListener("click", (event) => openTab(event, "https://github.com/IBRHUB/DLP"));
