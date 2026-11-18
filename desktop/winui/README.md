# MeshMonitor native Windows preview

A WinUI 3 interface for the MeshMonitor 4.18.0 backend. It opens to a native map with a source sidebar, plus Nodes, Messages, and Telemetry tabs. The window uses XAML controls throughout. It starts and stops the existing bundled Node.js server and uses its session, CSRF, permissions, and source-scoped APIs.

## Run the packaged build

Launch `MeshMonitor.Native.exe` from the Windows x64 package. Keep its DLLs and `resources` directory alongside the executable. It listens at `http://127.0.0.1:38081` and uses `%LOCALAPPDATA%\MeshMonitor.WinUI` by default. Quit the shell or any earlier build that uses that data directory first. To use a separate database, set `MESHMONITOR_WINUI_DATA` and `MESHMONITOR_WINUI_PORT` before launch.

The app uses the Windows 11 light theme. The map opens with Esri's color topographic basemap and can switch to Light Gray without resetting the viewport. Open **Legend** to see how marker colors reflect time since last heard and distinguish clusters. The map follows upstream access rules for positions. Sign in to add a Meshtastic TCP source, manage a connection, or transmit. The composer currently supports Meshtastic TCP and device-backed MeshCore with numeric channel selection and optional direct recipients. Backend acceptance does not establish radio delivery. Closing the window hides it in the tray. Choose **Menu > Quit MeshMonitor** to shut it down.

Select a source and open **Diagnostics** for its connection state and available node counts. **Edit source** lets an account with source write access rename, enable, or disable it. Editing connection settings also requires source read access.

On **Map**, search a node by name, short name, or ID. Choose a time range and select **Show trail** to view the recorded positions for the selected source.

Select a specific source and message, then choose **Delivery details** to see the backend's recorded events and their provenance. Use **Refresh events** to check for a later acknowledgment. On the Telemetry tab, select a reading and choose **Show history** to graph that node and metric over 1, 6, 24 hours, or 7 days.

To add a MeshCore source, choose **Add**, select **MeshCore**, and choose **USB / serial** or **TCP**. For USB, enter the COM port shown in Windows Device Manager. For TCP, enter the host and port. Select the device type and set **Connect automatically**. With source read and write access, **Edit source** can update these connection settings.

Choose **Meshtastic MQTT bridge** to add a receive-only bridge with one topic filter per line, or **MeshCore MQTT** to subscribe to an observer region. Supply the upstream broker address and any required username or password. On edits, a blank password keeps the stored password for the same broker. Changing the broker address clears the previous password unless you enter a replacement.

In a Meshtastic MQTT bridge's settings, expand **Bridge forwarding and topic filters** to choose receive-only, send-only, or bidirectional forwarding. Link a local Meshtastic MQTT broker to exchange traffic with its clients and choose single or per-gateway upstream connections. Incoming block and outgoing allow lists accept MQTT topic filters, one per line.

Choose **Embedded Meshtastic MQTT broker** or **Embedded MeshCore MQTT broker** to host a broker. New listeners default to `127.0.0.1`. Use a LAN bind address for external devices. The forms support static username and password authentication. Meshtastic brokers expose the root topic, and MeshCore brokers expose automatic startup. Check **Diagnostics** for the listener state and client count.

For Meshtastic direct messages, select **Acknowledgments** to see the local transmit acknowledgment and any recipient acknowledgment as separate events. The dialog refreshes while the backend is waiting for the recipient. If the backend supplies an acknowledgment sender, WinUI checks that it matches the destination before showing the recipient as acknowledged. Acknowledgment confirms delivery to a node and does not indicate that a person read the message.

Choose **Add favorite** or **Remove favorite** for a selected reading. The change is saved to that reading's source when your account has permission. **Favorites only** filters the latest list. **Compare favorites** plots a favorite node and metric across readable sources, with separate charts for different units.

In **Messages**, choose a channel or direct conversation from the selector, or use **Open conversation** to enter a channel index or recipient ID. Direct Meshtastic history uses the selected source's reported local node ID. In a Meshtastic conversation, select a message with a packet ID and choose **Reply**. For Meshtastic TCP channels, the selector shows available unread counts and **Mark read** updates that channel on the selected source.

Choose **Load older messages** in a channel or direct conversation. The app holds your view steady while you browse. Choose **Latest messages** to resume live refresh. If deletes or heavy activity move the last visible message out of the search window, return to latest messages before loading history again.

## Build from source

Use Windows x64, the .NET 8 SDK, and NuGet access. In `desktop/winui`, prepare the Windows runtime payload from the [upstream 4.18.0 release](https://github.com/Yeraze/meshmonitor/releases/tag/v4.18.0). Place the payload in `resources` with `package.json`, `binaries/node.exe`, and `dist` (including its `node_modules`). The project also accepts an absolute `MESHMONITOR_RESOURCES` path for development.

```powershell
.\resources\binaries\node.exe .\Prepare-DesktopProtos.mjs .\resources\dist
dotnet publish .\MeshMonitor.Native.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish
Copy-Item -LiteralPath .\resources -Destination .\artifacts\publish -Recurse
Copy-Item -LiteralPath .\MeshMonitor-LICENSE.txt -Destination .\artifacts\publish
```

When shipping the optional Apprise sidecar from the upstream installer, use `Patch-AppriseUtf8.mjs` with the Windows SDK manifest tool and the upstream executable. This gives the notification helper a per-process UTF-8 code page for redirected logs.

## Parity with MeshMonitor 4.18.0

| Area | In this preview | Remaining work |
| --- | --- | --- |
| Unified dashboard and source views | Native map, source filtering, node search and details, and time-filtered Meshtastic and MeshCore position trails with start and end markers. | Match layout and preferences, additional map layers, link overlays, asset playback, and offline maps |
| Nodes | Search, recent activity, signal, battery, and position details | Node administration, neighbors, traceroute, enrichment, and node history |
| Messaging | Recent messages, source-scoped channel and direct conversations with older-page navigation, Meshtastic replies and channel unread controls, recorded delivery status and event timelines | Reactions, direct-message unread controls, and protocol-specific channel controls |
| Telemetry | Latest readings, single-reading history, shared source favorites, and comparisons across sources with matching units | Configurable dashboards, axis settings, and comparison preferences |
| Administration | Local sign-in, authenticator codes, password changes, source renaming and enablement, Meshtastic TCP, MeshCore USB/serial and TCP, Meshtastic MQTT bridge with directional forwarding and topic filters, MeshCore MQTT ingest, embedded MQTT broker setup and editing, and connection diagnostics | OIDC, passkeys, broker observer allowlists and forwarding, bridge filter and rewrite controls, other source types, users, permissions, and backup/restore |
| Automation and analysis | Existing engine runs in the bundled backend | Native editors and views for rules, notifications, reports, packet analysis, and diagnostics |
| Desktop | Tray, backend lifecycle, data and log folders, bundled Windows executable | Installer, upgrade experience, and data migration workflows |

Use the same 4.18.0 database snapshot, accounts, and representative Meshtastic, MeshCore, and MQTT sources to verify parity for each workflow.
