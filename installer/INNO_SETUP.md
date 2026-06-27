# DLP Inno Setup Installer

This project uses Inno Setup to build a real Windows installer:

```text
dist\installer\DLP_Setup.exe
```

The installer does not use PowerShell, batch files, or manual registry commands for install logic.

## Expected Folder Structure Before Compiling

```text
DLP\
  app\
    DLP.csproj
    Program.cs
  browser-extension\
    manifest.json
    background.js
    content.js
    popup.html
    popup.js
  tools\
    yt-dlp.exe
    ffmpeg.exe
  dist\
    app\
      DLP.exe
      tools\
        yt-dlp.exe
        ffmpeg.exe
  installer\
    DLP_Setup.iss
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

The final installer is created at:

```text
dist\installer\DLP_Setup.exe
```

## Installer Behavior

- Installs DLP per-user into `%LOCALAPPDATA%\Programs\DLP`
- Copies `DLP.exe`, `yt-dlp.exe`, `ffmpeg.exe`, and browser extension files
- Generates the Native Messaging host manifest at install time
- Registers the Native Messaging host in HKCU for Chrome, Edge, and Brave
- Does not create Start Menu or desktop shortcuts
- Removes installed files, generated manifest, registry keys, and old DLP shortcuts on uninstall

The installer checks for Microsoft .NET 8 Windows Desktop Runtime x64 before installing because the app is framework-dependent.
