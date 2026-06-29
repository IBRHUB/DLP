# DLP

DLP is a Windows 10/11 x64 app and Chromium browser extension for sending supported media URLs from the browser to a local Windows app through Native Messaging

Architecture flow: [docs/DLP_FLOW.md](docs/DLP_FLOW.md)

## What It Does

- Adds a `Download with DLP` browser context menu
- Adds a small `DLP` button above supported media players
- Sends the current media URL to the local Windows app
- Opens the DLP window for manual video/audio download
- Supports silent download from the extension popup
- Skips duplicate downloads using media id detection
- Supports app self-update from GitHub Releases
- Supports optional Experimental mode for HTTPS pages with visible videos
- Uses `yt-dlp` for downloads
- Uses `ffmpeg` when media merging or audio conversion is needed

## Supported Sites

- YouTube and YouTube Shorts
- TikTok
- Instagram Reels
- X / Twitter
- SoundCloud

## Project Structure

```text
DLP/
  app/
    DLP.csproj
    Program.cs
  browser-extension/
    manifest.json
    background.js
    content.js
    popup.html
    popup.js
  installer/
    DLP_Setup.iss
    INNO_SETUP.md
  native-host/
    com.ibrhub.dlp.json
  tools/
    yt-dlp.exe
    ffmpeg.exe
  dist/
    app/
    installer/
      DLP_Setup.exe
```

## Build

Publish the app payload:

```cmd
dotnet publish app\DLP.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o dist\app
```

Compile the installer:

```cmd
ISCC.exe installer\DLP_Setup.iss
```

Output:

```text
dist\installer\DLP_Setup.exe
```

## Installer

Run:

```text
DLP_Setup.exe
```

Default install path:

```text
%LOCALAPPDATA%\Programs\DLP
```

Installed files:

- `DLP.exe`
- `tools\yt-dlp.exe`
- `tools\ffmpeg.exe`
- Browser extension files
- Generated Native Messaging host manifest

The installer registers the Native Messaging host under HKCU for:

- Chrome
- Edge
- Brave

## Browser Extension

Open the browser extensions page:

**Chrome**

```text
chrome://extensions
```

**Edge**

```text
edge://extensions
```

**Brave**

```text
brave://extensions
```

Enable Developer mode, choose Load unpacked, then select:

```text
%LOCALAPPDATA%\Programs\DLP\browser-extension
```

The unpacked extension has a fixed ID because `manifest.json` includes a stable public key:

```text
aaljempbfmhnhkdghllojlkmibdnmoeh
```

That ID must match `allowed_origins` in the Native Messaging manifest

## Native Messaging Flow

The extension sends messages from `background.js`:

```js
chrome.runtime.sendNativeMessage("com.ibrhub.dlp", payload)
```

Chromium browsers locate the host through these registry keys:

```text
HKCU\Software\Google\Chrome\NativeMessagingHosts\com.ibrhub.dlp
HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.ibrhub.dlp
HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.ibrhub.dlp
```

Each registry value points to:

```text
%LOCALAPPDATA%\Programs\DLP\native-host\com.ibrhub.dlp.json
```

Example generated manifest:

```json
{
  "name": "com.ibrhub.dlp",
  "description": "DLP Native Messaging Host",
  "path": "C:\\Users\\USER\\AppData\\Local\\Programs\\DLP\\DLP.exe",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://aaljempbfmhnhkdghllojlkmibdnmoeh/"
  ]
}
```

The browser launches `DLP.exe` as the Native Host and communicates through stdin/stdout

Native Messaging messages use:

- 4-byte little-endian message length
- UTF-8 JSON body
- stdout only for protocol responses

Example payload:

```json
{
  "action": "download",
  "url": "https://www.youtube.com/watch?v=VIDEO_ID",
  "source": "chrome-extension",
  "timestamp": "2026-06-27T00:00:00.000Z",
  "silent": false
}
```

Supported actions:

- `ping`
- `download`

The app accepts only absolute HTTPS URLs from supported hosts

## Download Modes

Manual mode:

- Click the `DLP` overlay button or browser context menu
- Choose `Download video` or `Download audio`
- Use `Update app` to install the latest `DLP_Setup.exe` from GitHub Releases
- Use `Update yt-dlp` in the app when a site stops working

Silent mode:

- Open the extension popup
- Enable `Silent download`
- Click the `DLP` overlay button
- DLP downloads directly without opening the app window

Experimental mode:

- Open the extension popup
- Enable `Experimental`
- DLP shows on HTTPS pages with visible videos
- Unsupported sites may fail if `yt-dlp` cannot read the page

Duplicate detection:

- DLP checks the media id before downloading
- If a file with the same id already exists in `Downloads\DLP`, the download is skipped
- If the same media is already downloading, the second request is skipped

Self update:

- DLP checks `IBRHUB/DLP` GitHub Releases
- It downloads the `DLP_Setup.exe` release asset
- It verifies the release SHA-256 digest when GitHub provides one
- It starts the installer and closes the app

Download folder:

```text
%USERPROFILE%\Downloads\DLP
```

## Logs

```text
%LOCALAPPDATA%\Programs\DLP\logs\DLP.log
```

`DLP.log` is the single application log. It includes app events, Native Messaging host events, yt-dlp output, direct download fallback events, update checks, duplicate checks, and clear error codes such as `missing_yt_dlp`, `http_forbidden`, `unsupported_media_url`, and `direct_download_failed`

## Known Security Prompts And Issues

- Windows SmartScreen may warn about `DLP_Setup.exe` because the installer is not code-signed
- Antivirus tools may quarantine `yt-dlp.exe` or `ffmpeg.exe`
- Native Messaging fails if the extension ID does not match `allowed_origins`
- Normal per-user installers cannot silently install unpacked Chromium extensions
- Experimental mode requires broader browser site access and is off by default
- After reloading the extension, refresh already-open media tabs
- Moving `DLP.exe` manually breaks Native Messaging because the manifest uses an absolute path
- The app requires Microsoft .NET 8 Windows Desktop Runtime x64 because it is framework-dependent
- Non-HTTPS URLs and unsupported hosts are rejected

## Official Sources

### Runtime and downloads

- Microsoft .NET 8: [https://dotnet.microsoft.com/en-us/download/dotnet/8.0](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- yt-dlp: [https://github.com/yt-dlp/yt-dlp](https://github.com/yt-dlp/yt-dlp)
- yt-dlp browser cookies option: `--cookies-from-browser BROWSER[+KEYRING][:PROFILE][::CONTAINER]`
- FFmpeg: [https://ffmpeg.org/download.html](https://ffmpeg.org/download.html)
- DLP Releases: [https://github.com/IBRHUB/DLP/releases](https://github.com/IBRHUB/DLP/releases)
- GitHub Releases API: [https://docs.github.com/en/rest/releases/releases](https://docs.github.com/en/rest/releases/releases)

### Browser extension

- Chrome Extension Manifest V3: [https://developer.chrome.com/docs/extensions/develop/migrate/what-is-mv3](https://developer.chrome.com/docs/extensions/develop/migrate/what-is-mv3)
- Chrome Native Messaging: [https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)
- Microsoft Edge Native Messaging: [https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)

### Build

- Inno Setup: [https://jrsoftware.org/isinfo.php](https://jrsoftware.org/isinfo.php)
