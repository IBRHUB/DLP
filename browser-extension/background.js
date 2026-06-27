const HOST_NAME = "com.ibrhub.dlp";
const MENU_ID = "dlp-download";
const DEFAULT_SETTINGS = {
  silentDownload: false
};

function getSettings(callback) {
  chrome.storage.local.get(DEFAULT_SETTINGS, callback);
}

function sendDownloadToNativeHost(url, callback) {
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
      source: "chrome-extension",
      timestamp: new Date().toISOString(),
      silent: Boolean(settings.silentDownload)
    };

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
  });
}

function createContextMenu() {
  chrome.contextMenus.create({
    id: MENU_ID,
    title: "Download with DLP",
    contexts: ["page", "link", "video"],
    documentUrlPatterns: [
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
    ]
  });
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    if (chrome.runtime.lastError) {
      console.log("DLP context menu cleanup failed:", chrome.runtime.lastError.message);
    }

    createContextMenu();
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_ID) {
    return;
  }

  const url = info.linkUrl || info.pageUrl || (tab && tab.url);

  sendDownloadToNativeHost(url);
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "dlp-download-current-video") {
    return false;
  }

  const url = message.url || (sender.tab && sender.tab.url);

  sendDownloadToNativeHost(url, sendResponse);
  return true;
});
