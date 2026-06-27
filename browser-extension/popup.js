const DEFAULT_SETTINGS = {
  silentDownload: false
};

const silentDownloadInput = document.getElementById("silentDownload");
const statusElement = document.getElementById("status");
const versionElement = document.getElementById("version");
const ibrahimLink = document.getElementById("ibrahimLink");
const sourceLink = document.getElementById("sourceLink");

versionElement.textContent = chrome.runtime.getManifest().version;

function setStatus(text) {
  statusElement.textContent = text;
}

function render(settings) {
  silentDownloadInput.checked = Boolean(settings.silentDownload);
  setStatus(settings.silentDownload ? "On" : "Off");
}

chrome.storage.local.get(DEFAULT_SETTINGS, (settings) => {
  if (chrome.runtime.lastError) {
    setStatus(chrome.runtime.lastError.message);
    return;
  }

  render(settings);
});

silentDownloadInput.addEventListener("change", () => {
  const silentDownload = silentDownloadInput.checked;

  chrome.storage.local.set({ silentDownload }, () => {
    if (chrome.runtime.lastError) {
      setStatus(chrome.runtime.lastError.message);
      return;
    }

    render({ silentDownload });
  });
});

function openTab(event, url) {
  event.preventDefault();
  chrome.tabs.create({ url });
}

ibrahimLink.addEventListener("click", (event) => openTab(event, "https://ibrhub.net"));
sourceLink.addEventListener("click", (event) => openTab(event, "https://github.com/IBRHUB/DLP"));
