# GameModeTray for Edifier X2s

A small Windows system tray app that automatically enables the **game mode** of
**Edifier X2s** earbuds every time they connect to the PC. The earbuds forget
the setting whenever they are switched off.

## Requirements

- Windows 10 version 2004 (build 19041) or later, with Bluetooth
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Edifier X2s earbuds paired with the PC (the device must be named `EDIFIER X2s`)

## Usage

Run `EdifierGameMode.exe`: a "G" icon appears in the system tray. From then on,
every time the earbuds connect, game mode is turned on within a few seconds.
Tick **Start with Windows** in the icon's menu to have it always running.

## How it works

- Finds the earbuds among the paired Bluetooth devices by name, then listens
  for their connect/disconnect event (WinRT
  `BluetoothDevice.ConnectionStatusChanged`), no polling.
- On connection it waits 3 seconds, sends the ON command, reads the state back
  and retries up to 3 times; on failure it shows a notification.
- Icon: grey = earbuds not connected, green = game mode on,
  orange = game mode off or error.
- Menu (right click): manual on/off, automatic activation on/off,
  start with Windows (`HKCU\...\Run` key), exit. Double click = turn on.
- Log in `%LOCALAPPDATA%\EdifierGameMode\log.txt`.

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

Only tested with the Edifier X2s. Other Edifier models may use the same framing
with different command codes.

## Build

Requires the .NET 10 SDK.

```
dotnet publish -c Release -o publish
```

Produces `publish\EdifierGameMode.exe` (single file, about 25 MB because of the
WinRT API projection).

## Disclaimer

This is an unofficial project, not affiliated with or endorsed by Edifier.
"Edifier" is a trademark of its respective owner. The protocol was worked out
by observing the Bluetooth traffic of the official app; use it at your own risk.

## License

[MIT](LICENSE)
