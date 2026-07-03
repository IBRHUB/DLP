const videosElement = document.getElementById("videos");
const refreshButton = document.getElementById("refreshVideos");
const downloadPathElement = document.getElementById("downloadPath");
const folderStatusElement = document.getElementById("folderStatus");
const unlockFolderButton = document.getElementById("unlockFolder");
const lockFolderButton = document.getElementById("lockFolder");
const viewerFrameElement = document.getElementById("viewerFrame");
const viewerMessageElement = document.getElementById("viewerMessage");
const viewerPlayButton = document.getElementById("viewerPlay");
const searchInput = document.getElementById("searchDownloads");
const sortSelect = document.getElementById("sortDownloads");
const filterButtons = Array.from(document.querySelectorAll("[data-filter]"));
const statCountElement = document.getElementById("statCount");
const statVisibleElement = document.getElementById("statVisible");
const statSizeElement = document.getElementById("statSize");
const fileDetailsElement = document.getElementById("fileDetails");
const detailsTitleElement = document.getElementById("detailsTitle");
const detailsGridElement = document.getElementById("detailsGrid");
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
let allFiles = [];
let currentFilter = "all";
let currentSearch = "";
let currentSort = "recent";

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

function formatFileCount(count) {
  return count === 1 ? "1 file" : `${count} files`;
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

function getModifiedTime(file) {
  const date = new Date(file?.modified || "");
  return Number.isNaN(date.getTime()) ? 0 : date.getTime();
}

function getFileTitle(file) {
  return file?.title || file?.fileName || "Untitled";
}

function getFileKind(file) {
  return isAudio(file) ? "audio" : "video";
}

function getSearchText(file) {
  return [
    file?.title,
    file?.fileName,
    file?.extension,
    file?.mediaType,
    formatSize(file?.sizeBytes),
    formatTime(file?.modified)
  ].filter(Boolean).join(" ").toLowerCase();
}

function matchesCurrentFilter(file) {
  if (currentFilter === "audio") {
    return isAudio(file);
  }

  if (currentFilter === "video") {
    return !isAudio(file);
  }

  return true;
}

function getVisibleFiles() {
  const query = currentSearch.trim().toLowerCase();
  const files = allFiles.filter((file) =>
    matchesCurrentFilter(file) && (!query || getSearchText(file).includes(query)));

  return files.sort((first, second) => {
    if (currentSort === "name") {
      return getFileTitle(first).localeCompare(getFileTitle(second), undefined, { sensitivity: "base" });
    }

    if (currentSort === "size") {
      return (Number(second.sizeBytes) || 0) - (Number(first.sizeBytes) || 0);
    }

    if (currentSort === "type") {
      return getFileKind(first).localeCompare(getFileKind(second))
        || getFileTitle(first).localeCompare(getFileTitle(second), undefined, { sensitivity: "base" });
    }

    return getModifiedTime(second) - getModifiedTime(first);
  });
}

function updateStats(visibleFiles) {
  const totalBytes = allFiles.reduce((sum, file) => sum + (Number(file?.sizeBytes) || 0), 0);

  statCountElement.textContent = formatFileCount(allFiles.length);
  statVisibleElement.textContent = `${visibleFiles.length} shown`;
  statSizeElement.textContent = formatSize(totalBytes) || "0 KB";
}

function createDetail(label, value) {
  if (!value) {
    return null;
  }

  const wrapper = document.createElement("div");
  wrapper.className = "detail";

  const term = document.createElement("dt");
  term.textContent = label;

  const description = document.createElement("dd");
  description.textContent = value;

  wrapper.append(term, description);
  return wrapper;
}

function renderSelectedDetails(file) {
  if (!fileDetailsElement || !detailsTitleElement || !detailsGridElement) {
    return;
  }

  if (!file) {
    fileDetailsElement.hidden = true;
    detailsTitleElement.textContent = "";
    detailsGridElement.replaceChildren();
    return;
  }

  detailsTitleElement.textContent = getFileTitle(file);
  detailsGridElement.replaceChildren(
    ...[
      createDetail("Type", getFileKind(file)),
      createDetail("Size", formatSize(file.sizeBytes)),
      createDetail("Modified", formatTime(file.modified)),
      createDetail("Extension", file.extension),
      createDetail("File", file.fileName)
    ].filter(Boolean)
  );
  fileDetailsElement.hidden = false;
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

function renderFolderAccess(folderAccess) {
  if (!folderStatusElement || !unlockFolderButton || !lockFolderButton) {
    return;
  }

  const access = folderAccess || {};
  const hasAccess = folderAccess && typeof folderAccess === "object";
  const isSupported = Boolean(hasAccess && access.isSupported !== false);
  const isUnlocked = Boolean(access.isUnlocked);
  const isBusy = Boolean(access.hasActiveOperations);
  let text = "Folder security unavailable";
  let state = "unsupported";

  if (isSupported && isUnlocked) {
    text = isBusy ? "Unlocked for download" : "Unlocked";
    state = "unlocked";
  } else if (isSupported) {
    text = "Locked";
    state = "locked";
  }

  folderStatusElement.textContent = text;
  folderStatusElement.dataset.state = state;
  unlockFolderButton.disabled = false;
  unlockFolderButton.textContent = isUnlocked ? "Open folder" : "Unlock & open";
  lockFolderButton.disabled = !isSupported || !isUnlocked || isBusy;
}

function unlockFolder() {
  unlockFolderButton.disabled = true;
  unlockFolderButton.textContent = "Opening";

  sendNativeCommand("unlock_download_folder", { open: true }, (response) => {
    renderFolderAccess(response.folderAccess);

    if (!response.ok) {
      unlockFolderButton.textContent = "Open failed";
      window.setTimeout(() => renderFolderAccess(response.folderAccess), 1400);
      return;
    }

    loadDownloads();
  });
}

function lockFolder() {
  lockFolderButton.disabled = true;
  lockFolderButton.textContent = "Locking";

  sendNativeCommand("lock_download_folder", {}, (response) => {
    renderFolderAccess(response.folderAccess);
    lockFolderButton.textContent = "Lock now";
    loadDownloads();
  });
}

function openDownload(fileName, button) {
  button.disabled = true;
  button.textContent = "...";

  sendNativeCommand("open_download", { fileName }, (response) => {
    button.disabled = false;
    button.textContent = response.ok ? "Open" : "Open failed";

    if (response.ok) {
      loadDownloads();
    }

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
    setPreviewMessage("Folder locked", file.fileName ? "Unlock downloads to preview or open it" : "Refresh downloads");
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

function getCompactFileDetails(file) {
  return [
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
  renderSelectedDetails(file);
  viewerFrameElement.setAttribute("aria-label", file.title || file.fileName || "Selected download");
  viewerPlayButton.disabled = !file.fileName;
  viewerPlayButton.textContent = "Open";
}

function clearViewer(message) {
  selectedFile = null;
  renderSelectedDetails(null);
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
  const badge = document.createElement("span");
  badge.className = "file-badge";
  badge.dataset.kind = getFileKind(file);
  badge.textContent = (file.extension || getFileKind(file)).replace(/^\./, "");
  row.appendChild(badge);
  item.appendChild(row);

  appendText(item, getCompactFileDetails(file), "meta ok");
  item.addEventListener("click", () => selectFile(file, item));
  item.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      selectFile(file, item);
    }
  });

  return item;
}

function renderDownloadList() {
  const visibleFiles = getVisibleFiles();
  const selectedFileName = selectedFile?.fileName;
  const nextSelection = visibleFiles.find((file) => file.fileName === selectedFileName) || visibleFiles[0] || null;

  videosElement.replaceChildren();
  updateStats(visibleFiles);

  if (allFiles.length === 0) {
    clearViewer("No downloads yet");
    appendText(videosElement, "No downloads yet", "empty");
    restoreInitialScroll();
    return;
  }

  if (visibleFiles.length === 0) {
    clearViewer("No matches");
    appendText(videosElement, "No matching downloads", "empty");
    restoreInitialScroll();
    return;
  }

  for (const file of visibleFiles) {
    const isSelected = file.fileName === nextSelection.fileName;
    videosElement.appendChild(createVideoItem(file, isSelected));
  }

  selectFile(nextSelection, videosElement.querySelector(".item.active") || videosElement.querySelector(".item"));
  restoreInitialScroll();
}

function renderError(message) {
  videosElement.replaceChildren();
  allFiles = [];
  updateStats([]);
  renderSelectedDetails(null);
  setPreviewMessage("Could not load downloads", "Check DLP and try Refresh");
  viewerFrameElement.setAttribute("aria-label", "Downloads could not be loaded");
  viewerPlayButton.disabled = true;
  viewerPlayButton.textContent = "Open";
  appendText(videosElement, message || "Could not load downloads", "empty");
  restoreInitialScroll();
}

function loadDownloads() {
  const previousText = refreshButton.textContent;
  refreshButton.disabled = true;
  refreshButton.textContent = "Refreshing";
  videosElement.replaceChildren();
  appendText(videosElement, "Loading", "empty");
  downloadPathElement.textContent = "";
  setPreviewMessage("Loading downloads", "Reading the DLP folder");
  viewerFrameElement.setAttribute("aria-label", "Loading downloads");
  viewerPlayButton.disabled = true;
  viewerPlayButton.textContent = "Open";

  sendNativeCommand("list_downloads", {}, (response) => {
    refreshButton.disabled = false;
    refreshButton.textContent = previousText || "Refresh";

    if (!response.ok) {
      renderError(response.message);
      return;
    }

    allFiles = Array.isArray(response.files) ? response.files.filter(Boolean) : [];
    downloadPathElement.textContent = response.directory || "";
    renderFolderAccess(response.folderAccess);
    renderDownloadList();
  });
}

refreshButton.addEventListener("click", loadDownloads);
unlockFolderButton.addEventListener("click", unlockFolder);
lockFolderButton.addEventListener("click", lockFolder);
searchInput.addEventListener("input", () => {
  currentSearch = searchInput.value || "";
  renderDownloadList();
});
sortSelect.addEventListener("change", () => {
  currentSort = sortSelect.value || "recent";
  renderDownloadList();
});
for (const button of filterButtons) {
  button.addEventListener("click", () => {
    currentFilter = button.dataset.filter || "all";

    for (const candidate of filterButtons) {
      const isActive = candidate === button;
      candidate.classList.toggle("is-active", isActive);
      candidate.setAttribute("aria-pressed", isActive ? "true" : "false");
    }

    renderDownloadList();
  });
}
viewerPlayButton.addEventListener("click", () => {
  if (selectedFile?.fileName) {
    openDownload(selectedFile.fileName, viewerPlayButton);
  }
});

loadDownloads();
