const videosElement = document.getElementById("videos");
const refreshButton = document.getElementById("refreshVideos");
const downloadPathElement = document.getElementById("downloadPath");
const viewerFrameElement = document.getElementById("viewerFrame");
const viewerMessageElement = document.getElementById("viewerMessage");
const viewerPlayButton = document.getElementById("viewerPlay");
const openedWithVideosHash = location.hash === "#videos";

if (openedWithVideosHash && history.replaceState) {
  history.replaceState(null, "", `${location.pathname}${location.search}`);
}

let selectedFile = null;
let previewVideoElement = null;
let previewAudioElement = null;
let expectedVideoUrl = "";
let previewSwitchTimer = 0;
let previewReadyTimer = 0;
let previewLoadToken = 0;
let restoredInitialScroll = false;

const PREVIEW_FADE_MS = 140;
const PREVIEW_READY_TIMEOUT_MS = 1200;

function formatSize(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return "";
  }

  if (bytes >= 1024 * 1024 * 1024) {
    return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`;
  }

  if (bytes >= 1024 * 1024) {
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  return `${Math.round(bytes / 1024)} KB`;
}

function formatTime(value) {
  const date = new Date(value || "");
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

function appendText(parent, text, className) {
  const element = document.createElement("div");
  element.textContent = text;

  if (className) {
    element.className = className;
  }

  parent.appendChild(element);
  return element;
}

function restoreInitialScroll() {
  if (restoredInitialScroll || !openedWithVideosHash) {
    return;
  }

  restoredInitialScroll = true;
  const resetScroll = () => window.scrollTo({ top: 0, left: 0, behavior: "auto" });

  window.requestAnimationFrame(() => {
    resetScroll();
    window.requestAnimationFrame(resetScroll);
  });
}

function sendNativeCommand(action, details, callback) {
  chrome.runtime.sendMessage({ type: "dlp-native-command", action, ...(details || {}) }, (response) => {
    if (chrome.runtime.lastError) {
      callback({
        ok: false,
        message: chrome.runtime.lastError.message
      });
      return;
    }

    callback(response || { ok: false, message: "DLP did not respond" });
  });
}

function openDownload(fileName, button) {
  button.disabled = true;
  button.textContent = "...";

  sendNativeCommand("open_download", { fileName }, (response) => {
    button.disabled = false;
    button.textContent = response.ok ? "Open" : "Open failed";

    if (!response.ok) {
      window.setTimeout(() => {
        button.textContent = "Open";
      }, 1600);
    }
  });
}

function isAudio(file) {
  return file.mediaType === "audio";
}

function resetPreviewFrame() {
  viewerFrameElement.classList.remove(
    "audio",
    "empty",
    "landscape",
    "loading",
    "portrait",
    "square",
    "switching",
    "tall",
    "ultrawide"
  );
  viewerFrameElement.style.removeProperty("--media-ratio");
}

function normalizePreviewUrl(url) {
  try {
    return new URL(url, window.location.href).href;
  } catch {
    return url || "";
  }
}

function clearPreviewTimers() {
  if (previewSwitchTimer) {
    window.clearTimeout(previewSwitchTimer);
    previewSwitchTimer = 0;
  }

  if (previewReadyTimer) {
    window.clearTimeout(previewReadyTimer);
    previewReadyTimer = 0;
  }
}

function ensurePreviewElements() {
  if (!previewVideoElement) {
    previewVideoElement = document.createElement("video");
    previewVideoElement.controls = true;
    previewVideoElement.preload = "metadata";
    previewVideoElement.playsInline = true;
    previewVideoElement.hidden = true;
    previewVideoElement.addEventListener("loadedmetadata", () => {
      if (!previewVideoElement.hidden && previewVideoElement.src === expectedVideoUrl) {
        applyVideoShape(previewVideoElement);

        if (previewReadyTimer) {
          window.clearTimeout(previewReadyTimer);
        }

        previewReadyTimer = window.setTimeout(() => {
          revealVideoPreview(previewLoadToken);
        }, PREVIEW_FADE_MS);
      }
    });
    previewVideoElement.addEventListener("loadeddata", () => {
      revealVideoPreview(previewLoadToken);
    });
    previewVideoElement.addEventListener("canplay", () => {
      revealVideoPreview(previewLoadToken);
    });
    previewVideoElement.addEventListener("error", () => {
      if (!previewVideoElement.hidden && previewVideoElement.src === expectedVideoUrl) {
        setPreviewMessage("Allow file access", "Enable file URLs for DLP in the extension details");
      }
    });
    viewerFrameElement.insertBefore(previewVideoElement, viewerPlayButton);
  }

  if (!previewAudioElement) {
    previewAudioElement = document.createElement("audio");
    previewAudioElement.controls = true;
    previewAudioElement.preload = "metadata";
    previewAudioElement.hidden = true;
    viewerFrameElement.insertBefore(previewAudioElement, viewerPlayButton);
  }
}

function setPreviewMessage(message, detail) {
  previewLoadToken += 1;
  clearPreviewTimers();
  expectedVideoUrl = "";
  ensurePreviewElements();
  resetPreviewFrame();
  viewerFrameElement.classList.add("empty");
  previewVideoElement.hidden = true;
  previewAudioElement.hidden = true;
  viewerMessageElement.hidden = false;
  viewerMessageElement.replaceChildren();

  const title = document.createElement("strong");
  title.textContent = message;
  viewerMessageElement.appendChild(title);

  if (detail) {
    const description = document.createElement("span");
    description.textContent = detail;
    viewerMessageElement.appendChild(description);
  }
}

function applyVideoShape(video) {
  const width = video.videoWidth || 16;
  const height = video.videoHeight || 9;
  const ratio = width / height;

  viewerFrameElement.style.setProperty("--media-ratio", `${width} / ${height}`);
  viewerFrameElement.classList.remove("tall", "portrait", "square", "landscape", "ultrawide");

  if (ratio < 0.68) {
    viewerFrameElement.classList.add("tall");
  } else if (ratio < 0.92) {
    viewerFrameElement.classList.add("portrait");
  } else if (ratio <= 1.12) {
    viewerFrameElement.classList.add("square");
  } else if (ratio > 2.05) {
    viewerFrameElement.classList.add("ultrawide");
  } else {
    viewerFrameElement.classList.add("landscape");
  }
}

function revealVideoPreview(loadToken) {
  if (loadToken !== previewLoadToken || previewVideoElement.hidden || previewVideoElement.src !== expectedVideoUrl) {
    return;
  }

  if (previewVideoElement.readyState >= 1) {
    applyVideoShape(previewVideoElement);
  } else {
    viewerFrameElement.classList.add("landscape");
  }

  if (previewReadyTimer) {
    window.clearTimeout(previewReadyTimer);
    previewReadyTimer = 0;
  }

  viewerFrameElement.classList.remove("switching");
  window.requestAnimationFrame(() => {
    if (loadToken === previewLoadToken && previewVideoElement.src === expectedVideoUrl) {
      viewerFrameElement.classList.remove("loading");
    }
  });
}

function renderPreview(file) {
  const loadToken = previewLoadToken + 1;
  previewLoadToken = loadToken;
  clearPreviewTimers();
  ensurePreviewElements();

  if (!file.fileUrl) {
    setPreviewMessage("Preview unavailable", file.fileName ? "Use Open to play it in Windows" : "Refresh downloads");
    return;
  }

  viewerMessageElement.hidden = true;

  if (isAudio(file)) {
    expectedVideoUrl = "";
    resetPreviewFrame();
    viewerFrameElement.classList.add("audio");
    previewVideoElement.pause();
    previewVideoElement.hidden = true;
    previewAudioElement.hidden = false;

    if (previewAudioElement.src !== file.fileUrl) {
      previewAudioElement.src = file.fileUrl;
      previewAudioElement.load();
    }

    return;
  }

  const nextUrl = normalizePreviewUrl(file.fileUrl);
  const isSameVideo = previewVideoElement.src === nextUrl;
  const hasVisibleVideo = !previewVideoElement.hidden && previewVideoElement.src && !viewerFrameElement.classList.contains("empty");

  previewAudioElement.hidden = true;
  previewAudioElement.pause();
  previewVideoElement.hidden = false;
  expectedVideoUrl = nextUrl;

  if (isSameVideo) {
    resetPreviewFrame();
    viewerMessageElement.hidden = true;
    previewAudioElement.hidden = true;
    previewVideoElement.hidden = false;

    if (previewVideoElement.readyState >= 1) {
      applyVideoShape(previewVideoElement);
    } else {
      viewerFrameElement.classList.add("landscape");
    }

    revealVideoPreview(loadToken);
    return;
  }

  viewerFrameElement.classList.add("loading", "switching");

  const loadVideo = () => {
    if (previewLoadToken !== loadToken) {
      return;
    }

    resetPreviewFrame();
    viewerFrameElement.classList.add("landscape", "loading", "switching");
    viewerMessageElement.hidden = true;
    previewAudioElement.hidden = true;
    previewVideoElement.hidden = false;
    previewVideoElement.pause();
    previewVideoElement.src = nextUrl;
    previewVideoElement.load();

    previewReadyTimer = window.setTimeout(() => {
      revealVideoPreview(loadToken);
    }, PREVIEW_READY_TIMEOUT_MS);
  };

  if (hasVisibleVideo) {
    previewSwitchTimer = window.setTimeout(loadVideo, PREVIEW_FADE_MS);
  } else {
    loadVideo();
  }
}

function getFileDetails(file) {
  return [
    file.extension || "",
    formatSize(file.sizeBytes),
    formatTime(file.modified)
  ].filter(Boolean).join("  |  ");
}

function selectFile(file, item) {
  selectedFile = file;

  for (const element of videosElement.querySelectorAll(".item.active")) {
    element.classList.remove("active");
    element.setAttribute("aria-pressed", "false");
  }

  if (item) {
    item.classList.add("active");
    item.setAttribute("aria-pressed", "true");
  }

  renderPreview(file);
  viewerFrameElement.setAttribute("aria-label", file.title || file.fileName || "Selected download");
  viewerPlayButton.disabled = !file.fileName;
  viewerPlayButton.textContent = "Open";
}

function clearViewer(message) {
  selectedFile = null;
  setPreviewMessage(message || "Select a file", "Choose a download from the list");
  viewerFrameElement.setAttribute("aria-label", "No download selected");
  viewerPlayButton.disabled = true;
  viewerPlayButton.textContent = "Open";
}

function createVideoItem(file, selected) {
  const item = document.createElement("article");
  item.className = "item";
  item.tabIndex = 0;
  item.setAttribute("role", "button");
  item.setAttribute("aria-pressed", selected ? "true" : "false");
  item.setAttribute("aria-label", file.title || file.fileName || "Downloaded file");

  if (selected) {
    item.classList.add("active");
  }

  const row = document.createElement("div");
  row.className = "row";

  appendText(row, file.title || file.fileName || "Untitled", "title");
  item.appendChild(row);

  appendText(item, getFileDetails(file), "meta ok");
  appendText(item, file.fileName || "", "meta");
  item.addEventListener("click", () => selectFile(file, item));
  item.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      selectFile(file, item);
    }
  });

  return item;
}

function renderError(message) {
  videosElement.replaceChildren();
  setPreviewMessage("Could not load downloads", "Check DLP and try Refresh");
  viewerFrameElement.setAttribute("aria-label", "Downloads could not be loaded");
  viewerPlayButton.disabled = true;
  viewerPlayButton.textContent = "Open";
  appendText(videosElement, message || "Could not load downloads", "empty");
  restoreInitialScroll();
}

function loadDownloads() {
  videosElement.replaceChildren();
  appendText(videosElement, "Loading", "empty");
  downloadPathElement.textContent = "";
  setPreviewMessage("Loading downloads", "Reading the DLP folder");
  viewerFrameElement.setAttribute("aria-label", "Loading downloads");
  viewerPlayButton.disabled = true;
  viewerPlayButton.textContent = "Open";

  sendNativeCommand("list_downloads", {}, (response) => {
    if (!response.ok) {
      renderError(response.message);
      return;
    }

    const files = Array.isArray(response.files) ? response.files : [];
    videosElement.replaceChildren();
    downloadPathElement.textContent = response.directory || "";

    if (files.length === 0) {
      clearViewer("No downloads yet");
      appendText(videosElement, "No downloads yet", "empty");
      restoreInitialScroll();
      return;
    }

    const firstFile = files[0] || {};

    for (const file of files) {
      const safeFile = file || {};
      videosElement.appendChild(createVideoItem(safeFile, safeFile.fileName === firstFile.fileName));
    }

    selectFile(firstFile, videosElement.querySelector(".item"));
    restoreInitialScroll();
  });
}

refreshButton.addEventListener("click", loadDownloads);
viewerPlayButton.addEventListener("click", () => {
  if (selectedFile?.fileName) {
    openDownload(selectedFile.fileName, viewerPlayButton);
  }
});

loadDownloads();
