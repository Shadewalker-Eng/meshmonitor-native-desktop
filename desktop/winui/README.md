# MeshMonitor native Windows preview

A WinUI 3 interface for the MeshMonitor 4.18.0 backend. It opens to a native map with a source sidebar, plus Nodes, Messages, and Telemetry tabs. The window uses XAML controls throughout. It starts and stops the existing bundled Node.js server and uses its session, CSRF, permissions, and source-scoped APIs.

## Version 27 workflows

Open **Tools** in the main window. Select the relevant source before editing a radio, channel, or integration. Available actions depend on source type and account permissions.

| Area | Available in the Windows preview |
| --- | --- |
| Nodes and radios | Filter nodes, manage ignored nodes, override positions, copy missing fields across sources, and compare recent configuration edits with device readback where supported. |
| Messages and channels | Search message history, insert node mentions, export CSV, browse unified channels and receptions, and manage virtual channels. |
| Automation | Build and edit trigger/action rules, configure block parameters, enable rules, and review execution history. |
| Notifications | Edit source and mesh-wide preferences, inspect delivery service status, and request a test notification. |
| Maps and telemetry | Edit markers and waypoints, import map layers, manage coverage surveys, and preview telemetry outliers before purging. |
| Analysis | Browse unified packets, inspect receptions, export packet records, and view RF and cross-source reports. |
| Source integrations | Configure Reticulum connections and send LXMF messages, edit MQTT topic rewrites and filters, and manage broker observers and forwarding targets. |

Some advanced settings and reports use JSON. Device readback requires a responding radio and supported firmware. MeshCore radio configuration remains deferred. End-to-end behavior with live radios and external services has not been verified for this release.

## Run the packaged build

Launch `MeshMonitor.Native.exe` from the Windows x64 package. Keep its DLLs and `resources` directory alongside the executable. It listens at `http://127.0.0.1:38081` and uses `%LOCALAPPDATA%\MeshMonitor.WinUI` by default. Quit the shell or any earlier build that uses that data directory first. To use a separate database, set `MESHMONITOR_WINUI_DATA` and `MESHMONITOR_WINUI_PORT` before launch.

The app uses the Windows 11 light theme. The map opens with Esri's color topographic basemap and can switch to Light Gray without resetting the viewport. Open **Legend** to see how marker colors reflect time since last heard and distinguish clusters. The map follows upstream access rules for positions. Sign in to add a Meshtastic TCP source, manage a connection, or transmit. The composer currently supports Meshtastic TCP and device-backed MeshCore with numeric channel selection and optional direct recipients. Backend acceptance does not establish radio delivery. Closing the window hides it in the tray. Choose **Menu > Quit MeshMonitor** to shut it down.

Select a specific source and message, then choose **Delivery details** to see the backend's recorded events and their provenance. Use **Refresh events** to check for a later acknowledgment. On the Telemetry tab, select a reading and choose **Show history** to graph that node and metric over 1, 6, 24 hours, or 7 days.

To add a MeshCore source, choose **Add**, select **MeshCore**, and choose **USB / serial** or **TCP**. For USB, enter the COM port shown in Windows Device Manager. For TCP, enter the host and port. Select the device type and set **Connect automatically**. With source read and write access, **Edit source** can update these connection settings.

Choose **Meshtastic MQTT bridge** to add a receive-only bridge with one topic filter per line, or **MeshCore MQTT** to subscribe to an observer region. Supply the upstream broker address and any required username or password. On edits, a blank password keeps the stored password for the same broker. Changing the broker address clears the previous password unless you enter a replacement.

In a Meshtastic MQTT bridge's settings, expand **Bridge forwarding and topic filters** to choose receive-only, send-only, or bidirectional forwarding. Link a local Meshtastic MQTT broker to exchange traffic with its clients and choose single or per-gateway upstream connections. Incoming block and outgoing allow lists accept MQTT topic filters, one per line.

Choose **Embedded Meshtastic MQTT broker** or **Embedded MeshCore MQTT broker** to host a broker. New listeners default to `127.0.0.1`. Use a LAN bind address for external devices. The forms support static username and password authentication. Meshtastic brokers expose the root topic, and MeshCore brokers expose automatic startup. Check **Diagnostics** for the listener state and client count.

For Meshtastic direct messages, select **Acknowledgments** to see the local transmit acknowledgment and any recipient acknowledgment as separate events. The dialog refreshes while the backend is waiting for the recipient. If the backend supplies an acknowledgment sender, WinUI checks that it matches the destination before showing the recipient as acknowledged. Acknowledgment confirms delivery to a node and does not indicate that a person read the message.

Choose **Add favorite** or **Remove favorite** for a selected reading. The change is saved to that reading's source when your account has permission. **Favorites only** filters the latest list. **Compare favorites** plots a favorite node and metric across readable sources, with separate charts for different units.

In **Messages**, choose a channel or direct conversation from the selector, or use **Open conversation** to enter a channel index or recipient ID. Direct Meshtastic history uses the selected source's reported local node ID. In a Meshtastic conversation, select a message with a packet ID and choose **Reply**. For Meshtastic TCP channels, the selector shows available unread counts and **Mark read** updates that channel on the selected source.

Choose **Load older messages** in a channel or direct conversation. The app holds your view steady while you browse. Choose **Latest messages** to resume live refresh. If deletes or heavy activity move the last visible message out of the search window, return to latest messages before loading history again.

On the map, select a positioned Meshtastic TCP node and choose **Neighbor links**. Arrows show direction, and the list gives signal strength and report time. Under **Nodes > Node diagnostics > Traceroute history**, choose **Show on map** beside a recorded route. Blue arrows show the forward path and purple arrows show the return path. The hop list includes nodes without visible positions. Choose **Hide links** to clear the overlay.

In **Messages**, the conversation selector includes unread direct peers for the selected Meshtastic TCP source. **Mark read** clears incoming messages for that peer. Select a channel or direct message and choose **React** to send an emoji. Reactions appear with the sender under the original message when both records are loaded.

In **Telemetry**, select a reading and choose **Pin chart**. Use **Pin comparison** in **Compare favorites** to add a cross-source chart to the Unified dashboard. The **Dashboard** tab shows the pinned charts for the selected source. Use **Move up**, **Move down**, or **Unpin** to change the layout. **Chart settings** saves the history window, automatic or fixed Y-axis bounds, and visible comparison sources. Each comparison uses a single unit. Dashboard and layer preferences are stored locally per account in the application data directory.

On the map, **Layers** controls node markers, position trails, neighbor links, and traceroutes independently. Load a trail or report with its existing action. Choose **Open node details** on a selected marker to open its source and select the corresponding node. Use **Telemetry history** there to choose a metric recorded within the past seven days. Meshtastic node diagnostics and remote administration remain available on eligible sources.

For Meshtastic TCP position playback, select a positioned node on the map, choose a history range, then **Show trail**. With **Layers → Position trails** enabled, the orange ring marks the selected historical fix. Choose **Play** to advance according to the recorded timestamps, or use the slider and arrow buttons to inspect a fix and its coordinates. Playback offers 1×, 60×, 600×, and 3600× speed. The ring stays on the last recorded position during gaps. Leaving the map pauses playback. Changing the node or history window clears the recording.

For **multi-node playback**, select a positioned Meshtastic TCP node and choose **Replay nodes**. Select two or more nodes on that source. The map numbers each marker, uses the selected history window, and shows how long ago each fix occurred on the shared playback clock. Nodes appear when their first fix is reached.

For **offline maps**, open **Map → Layers → Import offline tiles**. Choose a ZIP with XYZ raster tiles at its root, such as `12/655/1582.png`. PNG and JPEG tiles at zoom levels 0–16 are supported. Review the tile bounds and zoom levels, then choose **Use package**. The imported package is stored in the app data directory for your account. Use the basemap selector to switch back to it. Areas outside its coverage appear blank.

For the **packet monitor**, select a Meshtastic TCP source and open **Packets** with packet monitor read access. Choose a time window, optionally enter a sender ID and numeric port, then choose **Apply**. Select a packet for its recorded fields, **Pause** to hold the current page, or **Resume live** to return to recent traffic. **Activity report** uses the selected time window across the source. Select a packet type or one of the ten busiest senders to open matching records.

## Build from source

Use Windows x64, the .NET 8 SDK, and NuGet access. Supply the 4.18.0 Windows runtime payload from the [upstream release](https://github.com/Yeraze/meshmonitor/releases/tag/v4.18.0), laid out as `resources/package.json`, `resources/binaries/node.exe`, and `resources/dist`, including `node_modules`. Run `Prepare-DesktopProtos.mjs` once against the payload to fix the 4.18.0 package's Windows protobuf import paths. The project also accepts an absolute `MESHMONITOR_RESOURCES` path for development.

```powershell
dotnet publish .\MeshMonitor.Native.csproj -c Release --self-contained -o .\artifacts\publish
```

When shipping the optional Apprise sidecar from the upstream installer, use `Patch-AppriseUtf8.mjs` with the Windows SDK manifest tool and the upstream executable. This gives the notification helper a per-process UTF-8 code page for redirected logs.

## Radio configuration

Select a source and open **Radio configuration**. Meshtastic TCP sources expose the owner, device settings, firmware modules, and eight channel slots. Administrator access is required for channels, fixed coordinates, and Bluetooth and security writes. The Device UI section and fields with no supported write operation are read-only. Module and field availability depends on the device firmware.

MeshCore device sources expose name, LoRa, transmit power, coordinates, discovery, default scope, Bluetooth PIN, advert location, automatic contacts, direct-message acknowledgements, and telemetry sharing when the device reports the relevant settings. The editor reloads the selected section before sending a change. After a Meshtastic module save, the editor compares the edited fields with a response from the selected local radio. A mismatch lists the affected fields. A timeout leaves the save unconfirmed. For supported local device sections the editor requests fresh readback when permitted. Cached values are identified when a fresh read is unavailable. Serial HAL mode is writable under LoRa. The firmware defines dedicated Device UI read and store messages, but the bundled manager has no handler for their reply. Device UI remains read-only.

For remote Meshtastic settings, sign in as an administrator, select a connected Meshtastic TCP source, then select a remote node on the Nodes tab and choose **Administer selected radio**. Choose **Read device information** to request firmware, hardware model, role, and capability flags from the selected remote radio. Supported sections use the backend verified-save jobs. The panel distinguishes queued saves, routing acknowledgements, verified readback, differences, and timeouts. Owner, channels, and Security use command status where verified readback is unavailable. Queued jobs continue when the panel closes. Reopen the same remote node, select a submission under **Recent remote saves**, and choose **Check save status**. The app keeps the last 100 submissions from the current sign-in session, scoped to the source and node. Each check retrieves the current backend result. For an active verification job, **Stop verification retries** ends further retries. Any settings already applied on the radio stay in place. Full MeshCore companion and repeater configuration remains part of parity work.

## Parity with MeshMonitor 4.18.0

| Area | Available in the v27 Windows preview | Remaining work |
| --- | --- | --- |
| Dashboard and maps | Unified and per-source views, saved opening tab and sidebar width, position trails and playback, neighbor and traceroute overlays, offline XYZ tiles, markers, waypoints, imported GeoJSON/KML/KMZ layers, and coverage surveys. | Compare panel layouts, layer styling, and saved map preferences with the 4.18.0 dashboard. |
| Nodes | Search and filter nodes, inspect name, neighbor, and traceroute history, request fresh diagnostics, manage the ignore list, override positions, and preview and copy missing fields from another source. | Check history and management actions across source types, including ignore-list synchronization on a connected radio. |
| Radio administration | Edit supported local Meshtastic settings and channels, administer remote nodes, track recent saves, and compare supported changes with radio readback. The Device UI section is read-only. Basic MeshCore settings are available on supported devices. | Verify local and remote readback, mismatches, and timeouts on hardware. Device UI writes require backend support. Further MeshCore radio settings are deferred. |
| Messaging and channels | Browse unified and per-source conversations, search history and mentions, export CSV, inspect direct-message acknowledgments, and manage Meshtastic virtual channels and access. Meshtastic conversations also support replies, reactions, and unread controls. | Compare protocol-specific channel actions, especially for MeshCore. |
| Telemetry | View history, compare sources, pin and order charts, choose line or scatter views, set chart windows and bounds, and preview outliers before purging them. | Match remaining dashboard visualizations and chart interactions. |
| Automation and notifications | Create, edit, and enable rules, configure trigger and action blocks, and inspect run history. Edit source and mesh-wide notification preferences, inspect service status, and send a test notification. | Add native controls for rule branches and structured settings that use JSON. Verify delivery through configured external destinations. |
| Packets and RF reports | Browse source and unified packet history, inspect cross-source receptions, export filtered packets as JSONL, and view hop counts, RF coverage, direct contacts, mesh issues, solar nodes, and MQTT violations. | Add charts, sortable tables, and packet drill-down to reports that display JSON. |
| Source integrations | Configure Meshtastic TCP, MeshCore USB/serial or TCP, MQTT bridges and brokers, and Reticulum connections. Edit MQTT filters and topic rewrites, manage MeshCore broker observers and forwarding targets, and send LXMF messages. | Add field-based controls for advanced MQTT settings. Verify forwarding through external brokers and delivery through a Reticulum peer. |
| Accounts and permissions | Local sign-in, authenticator codes, password changes, and per-user virtual-channel access. | Add OIDC, passkeys, and user and role management. |
| Windows distribution and data | Windows x64 executable with bundled backend, tray controls, and shortcuts to data and logs. | Add an installer, upgrades, backup and restore, and data migration. |

The v27 Release build succeeded. Automated tests were skipped for this release. Live radio readback, notification delivery, and external forwarding still await end-to-end verification.
