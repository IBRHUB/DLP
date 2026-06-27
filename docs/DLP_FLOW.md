# DLP Flow

This file shows how DLP works between the browser, Windows Registry, Native Messaging, the app, and local storage

## Simple Filtergraphs

```text
┌──────────┬───────────────┐
│ browser  │               │          ┌─────────┐
╞══════════╡ media page    │ request  │ browser │ payload
│YouTube/X │               ├─────────►│ runtime ├─────►───╮
│TikTok... │               │          └─────────┘         │
└──────────┴───────────────┘                              │
                                  ╭───────────◄───────────╯
                                  │   ┌────────────────────────┐
                                  │   │  DLP request bridge    │
                                  │   ╞════════════════════════╡
                                  │   │  ┌───────┐  ┌───────┐  │
                                  ╰──►├─►│ host  ├─►│ app   ├─►├╮
                                      │  └───────┘  └───────┘  ││
                                      └────────────────────────┘│
                                                                │
                                                                │
┌──────────┬───────────────┐ media    ┌─────────┐               │
│ storage  │               │ files    │ yt-dlp  │               │
╞══════════╡ Downloads\DLP │◄─────────┤ ffmpeg  ├───────◄───────╯
│local disk│               │          │         │
│          │               │          └─────────┘
└──────────┴───────────────┘
```

## 1. Install Layout

```text
                         install DLP_Setup.exe
                                │
                                ▼
┌────────────────────────────────────────────────────────────────────┐
│ Windows user install path                                          │
╞════════════════════════════════════════════════════════════════════╡
│ {localappdata}\Programs\DLP                                        │
│                                                                    │
│  ┌──────────────────────────────┐                                  │
│  │ DLP.exe                      │  desktop app + native host       │
│  ├──────────────────────────────┤                                  │
│  │ tools\yt-dlp.exe             │  downloader engine               │
│  ├──────────────────────────────┤                                  │
│  │ tools\ffmpeg.exe             │  merge / audio conversion        │
│  ├──────────────────────────────┤                                  │
│  │ browser-extension\           │  unpacked extension files        │
│  ├──────────────────────────────┤                                  │
│  │ native-host\com.ibrhub.dlp.json                                 │
│  │                              │  Native Messaging manifest       │
│  └──────────────────────────────┘                                  │
└────────────────────────────────────────────────────────────────────┘
                                │
                                │ writes HKCU registry keys
                                ▼
┌────────────────────────────────────────────────────────────────────┐
│ Windows Registry                                                   │
╞════════════════════════════════════════════════════════════════════╡
│ HKCU\Software\Google\Chrome\NativeMessagingHosts\com.ibrhub.dlp    │
│ HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.ibrhub.dlp   │
│ HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\... │
│                                                                    │
│ default value                                                      │
│   {localappdata}\Programs\DLP\native-host\com.ibrhub.dlp.json      │
└────────────────────────────────────────────────────────────────────┘
```

## 2. Browser To App

```text
┌───────────────────────────┐
│ User opens supported site │
│ YouTube / TikTok / X      │
│ Instagram / SoundCloud    │
└─────────────┬─────────────┘
              │
              │ content.js detects media player
              ▼
┌───────────────────────────┐
│ DLP overlay button        │
│ or right-click menu       │
└─────────────┬─────────────┘
              │
              │ click
              ▼
┌───────────────────────────┐
│ Browser extension         │
╞═══════════════════════════╡
│ background.js             │
│ popup.js silent setting   │
│ chrome.runtime messaging  │
└─────────────┬─────────────┘
              │
              │ sendNativeMessage("com.ibrhub.dlp", payload)
              ▼
┌───────────────────────────┐
│ Chromium browser          │
╞═══════════════════════════╡
│ reads Windows Registry    │
│ opens native manifest     │
│ starts DLP.exe as host    │
└─────────────┬─────────────┘
              │
              │ Native Messaging stdio
              ▼
┌───────────────────────────┐
│ DLP.exe native host mode  │
╞═══════════════════════════╡
│ reads JSON from stdin     │
│ validates URL             │
│ returns JSON to stdout    │
└─────────────┬─────────────┘
              │
              │ ProcessStartInfo
              ▼
┌───────────────────────────┐
│ DLP.exe app mode          │
╞═══════════════════════════╡
│ manual window             │
│ or silent download        │
└───────────────────────────┘
```

## 3. Native Messaging Packet

```text
browser process
     │
     │ starts native host from manifest path
     ▼
┌──────────────────────────────────────────────────────────────┐
│ stdin                                                        │
╞══════════════════════════════════════════════════════════════╡
│ 4-byte little-endian length                                  │
├──────────────────────────────────────────────────────────────┤
│ UTF-8 JSON body                                              │
│ {                                                            │
│   "action": "download",                                      │
│   "url": "https://www.youtube.com/watch?v=...",              │
│   "source": "chrome-extension",                              │
│   "timestamp": "ISO-8601",                                   │
│   "silent": false                                            │
│ }                                                            │
└──────────────────────────────┬───────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────┐
│ DLP.exe validates                                            │
╞══════════════════════════════════════════════════════════════╡
│ absolute HTTPS URL                                           │
│ supported hostname only                                      │
│ no shell command                                             │
│ no URL execution                                             │
└──────────────────────────────┬───────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────┐
│ stdout                                                       │
╞══════════════════════════════════════════════════════════════╡
│ 4-byte little-endian length                                  │
├──────────────────────────────────────────────────────────────┤
│ UTF-8 JSON response                                          │
│ { "ok": true, "action": "download", "launched": true }       │
└──────────────────────────────────────────────────────────────┘
```

## 4. Manual Download

```text
             ┌────────────────────────┐
             │ DLP app window         │
 URL ───────►╞════════════════════════╡
             │ Download video         │
             │ Download audio         │
             │ Open folder            │
             │ Update app             │
             │ Update yt-dlp          │
             └───────────┬────────────┘
                         │
                         │ user chooses video or audio
                         ▼
┌──────────────────────────────────────────────────────────────┐
│ Duplicate Detector                                           │
╞══════════════════════════════════════════════════════════════╡
│ yt-dlp --skip-download --print id URL                        │
│ checks Downloads\DLP for existing [media_id] file            │
│ creates %LOCALAPPDATA%\DLP\locks\media_id.lock               │
└──────────────┬───────────────────────────────┬───────────────┘
               │                               │
               │ not duplicate                 │ duplicate
               ▼                               ▼
┌──────────────────────────────┐     ┌────────────────────────┐
│ yt-dlp.exe                   │     │ skip download          │
╞══════════════════════════════╡     │ status: Already done   │
│ best video + best audio      │     └────────────────────────┘
│ or best audio only           │
└──────────────┬───────────────┘
               │
               │ needs merge or audio conversion
               ▼
┌──────────────────────────────┐
│ ffmpeg.exe                   │
╞══════════════════════════════╡
│ merge mp4                    │
│ convert mp3                  │
└──────────────┬───────────────┘
               │
               ▼
┌──────────────────────────────┐
│ %USERPROFILE%\Downloads\DLP  │
╞══════════════════════════════╡
│ title [media_id].mp4         │
│ title [media_id].mp3         │
└──────────────────────────────┘
```

## 5. Silent Download

```text
┌────────────────────────────┐
│ Extension popup            │
╞════════════════════════════╡
│ Silent download: on        │
└──────────────┬─────────────┘
               │
               │ DLP button click
               ▼
┌────────────────────────────┐
│ Native host payload        │
╞════════════════════════════╡
│ "silent": true             │
└──────────────┬─────────────┘
               │
               │ DLP.exe --silent --url URL
               ▼
┌────────────────────────────┐
│ no app window              │
╞════════════════════════════╡
│ duplicate check            │
│ yt-dlp download            │
│ ffmpeg if needed           │
└──────────────┬─────────────┘
               │
               ▼
┌────────────────────────────┐
│ Downloads\DLP              │
│ app.log                    │
└────────────────────────────┘
```

## 6. Self Update

```text
             ┌────────────────────────┐
             │ DLP app window         │
 user click  ╞════════════════════════╡
────────────►│ Update app             │
             └───────────┬────────────┘
                         │
                         │ GitHub API
                         ▼
┌──────────────────────────────────────────────────────────────┐
│ https://api.github.com/repos/IBRHUB/DLP/releases/latest      │
╞══════════════════════════════════════════════════════════════╡
│ latest tag                                                   │
│ release assets                                               │
│ DLP_Setup.exe                                                │
│ sha256 digest when provided                                  │
└──────────────┬───────────────────────────────────────────────┘
               │
               │ download installer
               ▼
┌──────────────────────────────────────────────────────────────┐
│ %LOCALAPPDATA%\DLP\updates\DLP_Setup.exe                     │
└──────────────┬───────────────────────────────────────────────┘
               │
               │ verify SHA-256
               ▼
┌──────────────────────────────────────────────────────────────┐
│ start installer                                              │
╞══════════════════════════════════════════════════════════════╡
│ /CURRENTUSER                                                 │
│ /SILENT                                                      │
│ /SUPPRESSMSGBOXES                                            │
│ /NORESTART                                                   │
│ /CLOSEAPPLICATIONS                                           │
└──────────────┬───────────────────────────────────────────────┘
               │
               │ app closes
               ▼
┌──────────────────────────────────────────────────────────────┐
│ installed files are replaced in                              │
│ {localappdata}\Programs\DLP                                  │
└──────────────────────────────────────────────────────────────┘
```

## 7. Saved Paths

```text
┌──────────────────────────────┬──────────────────────────────────────────────┐
│ item                         │ path                                         │
╞══════════════════════════════╪══════════════════════════════════════════════╡
│ installed app                │ %LOCALAPPDATA%\Programs\DLP                 │
├──────────────────────────────┼──────────────────────────────────────────────┤
│ downloaded media             │ %USERPROFILE%\Downloads\DLP                 │
├──────────────────────────────┼──────────────────────────────────────────────┤
│ app log                      │ %LOCALAPPDATA%\DLP\logs\app.log             │
├──────────────────────────────┼──────────────────────────────────────────────┤
│ native host log              │ %LOCALAPPDATA%\DLP\logs\native-host.log     │
├──────────────────────────────┼──────────────────────────────────────────────┤
│ duplicate locks              │ %LOCALAPPDATA%\DLP\locks                    │
├──────────────────────────────┼──────────────────────────────────────────────┤
│ self-update installer cache  │ %LOCALAPPDATA%\DLP\updates                  │
├──────────────────────────────┼──────────────────────────────────────────────┤
│ native host manifest         │ %LOCALAPPDATA%\Programs\DLP\native-host     │
├──────────────────────────────┼──────────────────────────────────────────────┤
│ extension files              │ %LOCALAPPDATA%\Programs\DLP\browser-extension│
└──────────────────────────────┴──────────────────────────────────────────────┘
```

## 8. Security Boundary

```text
┌──────────────────────────────────────────────────────────────┐
│ untrusted browser URL                                        │
└──────────────┬───────────────────────────────────────────────┘
               │
               │ validate
               ▼
┌──────────────────────────────────────────────────────────────┐
│ DLP accepts only                                             │
╞══════════════════════════════════════════════════════════════╡
│ HTTPS                                                        │
│ supported hostnames                                          │
│ structured ProcessStartInfo arguments                        │
│ Native Messaging stdout only for protocol JSON               │
│ file logs only                                               │
└──────────────┬───────────────────────────────────────────────┘
               │
               │ reject everything else
               ▼
┌──────────────────────────────────────────────────────────────┐
│ no arbitrary command execution                               │
│ URL is never executed as a command                           │
│ shell is not used for downloads                              │
└──────────────────────────────────────────────────────────────┘
```

