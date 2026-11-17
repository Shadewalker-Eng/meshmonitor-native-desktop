# MeshMonitor WinUI 3 shell

An unpackaged x64 Windows app that launches the existing MeshMonitor Node.js server and displays the React dashboard inside a WinUI 3 WebView2 window. The shell waits for `/api/health`, surfaces startup errors with a retry button, and stops its backend when the window closes.

## Build and run

Use Windows 10 build 19041 or later, the .NET 8 SDK, and the Microsoft Edge WebView2 Runtime. Building requires access to NuGet. For IDE development, open the project in Visual Studio with the WinUI application development workload. [WinUI setup](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here) · [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)

Set `MESHMONITOR_RESOURCES` to a prepared Windows MeshMonitor desktop payload containing `package.json`, `binaries/node.exe`, `dist/index.html`, `dist/server/server.js`, and the production dependencies in `dist/node_modules`. The shell also checks for a `resources` folder beside its executable.

You can extract the payload from the [official MeshMonitor 4.18.0 Windows installer](https://github.com/Yeraze/meshmonitor/releases/tag/v4.18.0) with 7-Zip. Run this from the project directory, with the installer file in the same directory.

```powershell
7z x .\MeshMonitor-Desktop-4.18.0-x64-setup.exe '-o.\obj\meshmonitor-runtime' 'package.json' 'dist\*' 'binaries\node.exe'
```

To prepare the payload from source, install a current x64 Node.js 24 release with npm, Python, and the Visual Studio C++ build tools for native npm dependencies. Initialize the upstream submodules and run the staging script. Replace the repository path with your checkout.

```powershell
git -C C:\src\meshmonitor submodule update --init --recursive
.\Prepare-Resources.ps1 -Repository C:\src\meshmonitor -Destination .\obj\meshmonitor-runtime
```

With the payload available, build and run from PowerShell.

```powershell
$env:MESHMONITOR_RESOURCES = (Resolve-Path .\obj\meshmonitor-runtime).Path
dotnet build .\MeshMonitor.WinUI.csproj --artifacts-path .\artifacts
& .\artifacts\bin\MeshMonitor.WinUI\debug_win-x64\MeshMonitor.WinUI.exe
```

The payload layout follows the upstream [Windows desktop CI workflow](https://github.com/Yeraze/meshmonitor/blob/2cca4c6f4a9ec60f939ce8d215327f501cffc465/.github/workflows/desktop-ci.yml#L55-L111).

## Prototype behavior

- The backend binds to `127.0.0.1:38081`. Set `MESHMONITOR_WINUI_PORT` before launch to change the port.
- The prototype starts with its own SQLite database and logs under `%LOCALAPPDATA%\MeshMonitor.WinUI`. Set `MESHMONITOR_WINUI_DATA` before launch to use a different data directory. Sign in and configure sources from the dashboard.
- The shell currently supports the local sign-in flow. External single sign-on and callback handling, tray operation, the Apprise sidecar, migration from the Tauri app, and installer packaging are outside this prototype.

For a full smoke test, launch the app twice to check the single-instance guard, sign in, add a source, verify live updates, and close the window. Confirm the backend port is released and relaunch to confirm the dashboard retains its data.
