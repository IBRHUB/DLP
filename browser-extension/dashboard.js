const videosElement = document.getElementById("videos");
const refreshButton = document.getElementById("refreshVideos");
const downloadPathElement = document.getElementById("downloadPath");
const viewerFrameElement = document.getElementById("viewerFrame");
const viewerTitleElement = document.getElementById("viewerTitle");
const viewerMetaElement = document.getElementById("viewerMeta");
const viewerFileElement = document.getElementById("viewerFile");
const viewerPlayButton = document.getElementById("viewerPlay");

let selectedFile = null;

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
    button.textContent = response.ok ? "Play" : "ERR";

    if (!response.ok) {
      window.setTimeout(() => {
        button.textContent = "Play";
      }, 1600);
    }
  });
}

function isAudio(file) {
  return file.mediaType === "audio";
}

function renderPreview(file) {
  viewerFrameElement.replaceChildren();

  if (!file.fileUrl) {
    viewerFrameElement.textContent = "Preview unavailable";
    return;
  }

  if (isAudio(file)) {
    const audio = document.createElement("audio");
    audio.controls = true;
    audio.preload = "metadata";
    audio.src = file.fileUrl;
    viewerFrameElement.appendChild(audio);
    return;
  }

  const video = document.createElement("video");
  video.controls = true;
  video.preload = "metadata";
  video.src = file.fileUrl;
  video.addEventListener("loadedmetadata", () => {
    video.classList.toggle("portrait", video.videoHeight > video.videoWidth);
    video.classList.toggle("landscape", video.videoWidth >= video.videoHeight);
  });
  video.addEventListener("error", () => {
    viewerFrameElement.textContent = "Enable file access for DLP";
  });

  viewerFrameElement.appendChild(video);
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
  }

  if (item) {
    item.classList.add("active");
  }

  renderPreview(file);
  viewerTitleElement.textContent = file.title || file.fileName || "Untitled";
  viewerMetaElement.textContent = getFileDetails(file);
  viewerFileElement.textContent = file.fileName || "";
  viewerPlayButton.disabled = !file.fileName;
  viewerPlayButton.textContent = "Play";
}

function clearViewer(message) {
  selectedFile = null;
  viewerFrameElement.replaceChildren();
  viewerFrameElement.textContent = message || "Select a video";
  viewerTitleElement.textContent = "No video selected";
  viewerMetaElement.textContent = "";
  viewerFileElement.textContent = "";
  viewerPlayButton.disabled = true;
  viewerPlayButton.textContent = "Play";
}

function createVideoItem(file, selected) {
  const item = document.createElement("article");
  item.className = "item";

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

  return item;
}

function renderError(message) {
  videosElement.replaceChildren();
  clearViewer("No videos");
  appendText(videosElement, message || "Could not load downloads", "empty");
}

function loadDownloads() {
  videosElement.replaceChildren();
  appendText(videosElement, "Loading", "empty");
  downloadPathElement.textContent = "";
  clearViewer("Loading");

  sendNativeCommand("list_downloads", {}, (response) => {
    if (!response.ok) {
      renderError(response.message);
      return;
    }

    const files = Array.isArray(response.files) ? response.files : [];
    videosElement.replaceChildren();
    downloadPathElement.textContent = response.directory || "";

    if (files.length === 0) {
      clearViewer("No videos");
      appendText(videosElement, "No downloaded videos yet", "empty");
      return;
    }

    const firstFile = files[0] || {};

    for (const file of files) {
      const safeFile = file || {};
      videosElement.appendChild(createVideoItem(safeFile, safeFile.fileName === firstFile.fileName));
    }

    selectFile(firstFile, videosElement.querySelector(".item"));
  });
}

refreshButton.addEventListener("click", loadDownloads);
viewerPlayButton.addEventListener("click", () => {
  if (selectedFile?.fileName) {
    openDownload(selectedFile.fileName, viewerPlayButton);
  }
});

loadDownloads();
