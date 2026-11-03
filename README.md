# GameModeTray

A small Windows system tray app that automatically enables the **game mode** of
**Edifier X2s** earbuds every time they connect to the PC. The earbuds forget
the setting whenever they are switched off.

## How it works

- Listens for the earbuds' connect/disconnect event (WinRT
  `BluetoothDevice.ConnectionStatusChanged`), no polling.
- On connection it waits 3 seconds, sends the ON command, reads the state back
  and retries up to 3 times; on failure it shows a notification.
- Icon: grey = earbuds not connected, green = game mode on,
  orange = game mode off or error.
- Menu (right click): manual on/off, automatic activation on/off,
  start with Windows (`HKCU\...\Run` key), exit. Double click = turn on.
- Log in `%LOCALAPPDATA%\EdifierGameMode\log.txt`.

The earbuds' address is hard-coded in `TrayContext.cs` (`Address`).

## Protocol

Reverse-engineered from the Bluetooth HCI log of the Edifier Connect app on
Android. Commands travel over RFCOMM (SPP), channel 2, of the classic Bluetooth
connection.

```
AA EC <cmd> <data length, 2 bytes BE> <data> <checksum>
```

- `AA` = host -> earbuds, `BB` = earbuds -> host
- checksum = sum of all preceding bytes, keeping the lowest byte

| command | bytes |
|---|---|
| game mode ON | `AA EC 09 00 01 01 A1` |
| game mode OFF | `AA EC 09 00 01 00 A0` |
| read state | `AA EC 08 00 00 9E` → reply `BB EC 08 00 02 <01/00> 00 <chk>` |

## Build

Requires the .NET 10 SDK; the exe runs on the installed Windows Desktop 10
runtime.

```
dotnet publish -c Release -o publish
```

Produces `publish\EdifierGameMode.exe` (single file, about 25 MB because of the
WinRT API projection).
