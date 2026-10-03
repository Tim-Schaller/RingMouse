# Manual test checklist

The unit tests (`.\build.ps1 -Target Test`) cover the protocol, the core logic and the DeviceService against a simulated
mouse. Everything that needs a real mouse, Windows UI or other apps is checked by hand with this list. Quit Logi Options+
first (see the README).

Tip: to test a fresh installation without touching your own configuration, quit RingMouse and start it with an empty data
folder:

```powershell
$env:RINGMOUSE_HOME = "$env:TEMP\RingMouse-Test"; & ".\RingMouse.exe"; Remove-Item Env:RINGMOUSE_HOME
```

| # | Test | Expected |
|---|---|---|
| 1 | `ringmouse-probe list`, `dump` | The mouse is found; features, buttons and battery are shown |
| 2 | `ringmouse-probe live --cid <CID> --rawxy`, then press/hold/move that button | `PRESSED`/`released` appear, Raw-XY sums while holding; after Ctrl+C the button is "restored" |
| 3 | `ringmouse-probe watch --cid <CID> --rawxy --takeover`, then mouse off/on, Bluetooth off/on, standby, lock/unlock | `unreachable` → `Wireless status: status=reconnect …` → `configured (Reconnect (0x1D4B))`; the button works again right away |
| 4 | Start RingMouse for the first time (empty data folder) | The first-run setup asks for the ring button; after pressing one it says "Done!". "Other button" replaces it |
| 5 | Settings → Buttons → **Press button …**, then press e.g. the back button | The list jumps to `0x0053 Back`; the browser does **not** go back meanwhile; afterwards the back button works normally again |
| 6 | Tap the ring button **briefly** | The ring stays open; clicking "Emoji" opens the emoji panel in the previous window |
| 7 | **Hold** the ring button, choose a direction, release | The action runs; with Raw-XY the pointer stands still meanwhile |
| 8 | Submenu *Text* → *Date* in Notepad or a browser | Today's date is inserted |
| 9 | Submenu *Media* → *Volume up* / *Next track* while music plays | Volume or track changes, the focus stays in the window |
| 10 | Open the ring at the screen edge and on a second monitor | The ring stays fully visible and sharp |
| 11 | Elevated PowerShell in the foreground, then a ring text snippet | Without uiAccess: tray note and log entry (UIPI). With uiAccess: the text arrives |
| 12 | Battery: charge the mouse | Green bolt in the tray, "charging complete" at the end |
| 13 | Change `config.json` (e.g. `"radius": 180`) and save | Applied immediately; a typo gives a tray message with the line, the old configuration stays active |
| 14 | Settings → General → Language: switch between English and German, restart | Settings, tray menu, notifications and the setup window appear in the chosen language |
| 15 | Settings → General → Restore defaults | A backup `config.backup-*.json` is created next to `config.json` |
| 16 | Tray → Exit | The ring button has its native function again (e.g. DPI switching) |

The log is in `%APPDATA%\RingMouse\logs\ringmouse-*.log`. For problems, enable the **raw HID++ log** in the tray menu and
look at `hidpp-*.log`.
