const HOST_NAME = "com.ibrhub.dlp";
const MENU_ID = "dlp-download";

const DEFAULT_SETTINGS = {
  silentDownload: false,
  autoHideOverlay: true,
  overlayPosition: "auto",
  experimentalAllSites: false
};

const SUPPORTED_DOCUMENT_URL_PATTERNS = [
  "*://youtube.com/*",
  "*://www.youtube.com/*",
  "*://m.youtube.com/*",
  "*://youtu.be/*",
  "*://tiktok.com/*",
  "*://www.tiktok.com/*",
  "*://m.tiktok.com/*",
  "*://vm.tiktok.com/*",
  "*://vt.tiktok.com/*",
  "*://instagram.com/*",
  "*://www.instagram.com/*",
  "*://m.instagram.com/*",
  "*://x.com/*",
  "*://www.x.com/*",
  "*://mobile.x.com/*",
  "*://twitter.com/*",
  "*://www.twitter.com/*",
  "*://mobile.twitter.com/*",
  "*://soundcloud.com/*",
  "*://www.soundcloud.com/*",
  "*://m.soundcloud.com/*",
  "*://on.soundcloud.com/*"
];

const EXPERIMENTAL_DOCUMENT_URL_PATTERNS = [
  "https://*/*"
];

function getSettings(callback) {
  chrome.storage.local.get(DEFAULT_SETTINGS, callback);
}

function getSafeHttpsUrl(url) {
  if (!url) {
    return null;
  }

  try {
    const parsed = new URL(url);
    return parsed.protocol === "https:" ? parsed.href : null;
  } catch {
    return null;
  }
}

function getContextMenuUrl(info, tab) {
  return info.linkUrl
    || getSafeHttpsUrl(info.srcUrl)
    || info.pageUrl
    || (tab && tab.url);
}

function sendNativePayload(payload, callback) {
  chrome.runtime.sendNativeMessage(HOST_NAME, payload, (response) => {
    if (chrome.runtime.lastError) {
      const error = {
        ok: false,
        error: "native_host_error",
        message: chrome.runtime.lastError.message
      };

      console.log("DLP native host error:", chrome.runtime.lastError.message);

      if (callback) {
        callback(error);
      }

      return;
    }

    console.log("DLP native host response:", response);

    if (callback) {
      callback(response);
    }
  });
}

function sendDownloadToNativeHost(url, options, callback) {
  if (typeof options === "function") {
    callback = options;
    options = {};
  }

  const details = options || {};

  if (!url) {
    const error = {
      ok: false,
      error: "missing_url",
      message: "DLP could not determine a supported video URL"
    };

    console.log(error.message);

    if (callback) {
      callback(error);
    }

    return;
  }

  getSettings((settings) => {
    const payload = {
      action: "download",
      url,
      title: details.title || "",
      source: "chrome-extension",
      timestamp: new Date().toISOString(),
      silent: Boolean(settings.silentDownload),
      experimental: Boolean(settings.experimentalAllSites)
    };

    sendNativePayload(payload, callback);
  });
}

function sendNativeCommand(action, details, callback) {
  if (typeof details === "function") {
    callback = details;
    details = {};
  }

  sendNativePayload({
    action,
    ...(details || {}),
    source: "chrome-extension",
    timestamp: new Date().toISOString()
  }, callback);
}

function createContextMenu() {
  getSettings((settings) => {
    const documentUrlPatterns = settings.experimentalAllSites
      ? EXPERIMENTAL_DOCUMENT_URL_PATTERNS
      : SUPPORTED_DOCUMENT_URL_PATTERNS;

    chrome.contextMenus.create({
      id: MENU_ID,
      title: "Download with DLP",
      contexts: ["page", "link", "video"],
      documentUrlPatterns
    });
  });
}

function refreshContextMenu() {
  chrome.contextMenus.removeAll(() => {
    if (chrome.runtime.lastError) {
      console.log("DLP context menu cleanup failed:", chrome.runtime.lastError.message);
    }

    createContextMenu();
  });
}

chrome.runtime.onInstalled.addListener(() => {
  refreshContextMenu();
});

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName === "local" && Object.prototype.hasOwnProperty.call(changes, "experimentalAllSites")) {
    refreshContextMenu();
  }
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_ID) {
    return;
  }

  const url = getContextMenuUrl(info, tab);

  sendDownloadToNativeHost(url, {
    title: tab?.title || ""
  });
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message !== "object") {
    return false;
  }

  if (message.type === "dlp-native-command") {
    sendNativeCommand(message.action, {
      fileName: message.fileName || ""
    }, sendResponse);
    return true;
  }

  if (message.type === "dlp-download-current-video") {
    const url = message.url || (sender.tab && sender.tab.url);
    const title = message.title || (sender.tab && sender.tab.title) || "";

    sendDownloadToNativeHost(url, { title }, sendResponse);
    return true;
  }

  return false;
});
