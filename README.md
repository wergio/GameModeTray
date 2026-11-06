# GameModeTray

A Windows system tray app that automatically turns on the **game mode**
(low-latency mode) of your Bluetooth headphones every time they connect to your
PC. Usefull because many headphones forget the setting whenever they are switched off.

**It ships with profiles for the Edifier X2s and the Realme Buds T200 Lite**. Other headphones can be added
from the Advanced device settings window, with the [guided detection](#option-a-guided-detection) (experimental)
or by entering the bytes their companion app sends: the [manual guide](#option-b-manual-procedure) below shows how to find them.

## Download

Get `GameModeTray.exe` from the [Releases page](https://github.com/wergio/GameModeTray/releases/latest).
It is a single file with nothing to install: put it in a folder of your choice and start it.
To update, replace the file.

The file is not code-signed yet, so Windows SmartScreen may show "Windows protected your
PC": choose **More info → Run anyway**.

## Requirements

- Windows 10 version 2004 (build 19041) or later, 64-bit, with Bluetooth
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or later)
- Headphones paired with the PC whose companion app talks over classic
  Bluetooth RFCOMM (SPP). 
- BLE GATT is not supported yet (ask me if you need it, but I don't own any headphones using this protocol).

## Usage

Run `GameModeTray.exe`: a "G" icon appears in the system tray. On the first run
it looks among the paired Bluetooth devices for headphones that have a
[ready-made profile](#profiles) and uses it; if there are none, it opens the
advanced device settings. From then on, every time the headphones connect, game
mode is turned on within a few seconds.

- Icon: grey = headphones not connected, green = game mode on,
  orange = game mode off or error.
- Menu (right click): headphones name and state, turn game mode on/off, check
  current status, enable automatically on connect, start with Windows, advanced
  device settings, exit. Double click = switch state.
- Settings are stored in `%APPDATA%\GameModeTray\settings.json`, the log in
  `%LOCALAPPDATA%\GameModeTray\log.txt`.

## Advanced device settings

In this menu you can specify a different or custom headphone configuration

| field | meaning |
|---|---|
| Preset | Fills in all the fields from a [ready-made profile](#profiles). Editing any field switches it to **Custom**, which keeps the fields as they are |
| Profile name | Shown in the tray tooltip and notifications |
| Bluetooth device | Name of the paired headphones (pick from the list) |
| RFCOMM channel | Channel the companion app uses for its commands |
| ON command | Bytes that turn game mode on |
| OFF command | Bytes that turn it off (optional, for the menu) |
| State query | Bytes that ask the headphones for the current state (optional) |
| State reply when ON / OFF | What the headphones answer in each state; `??` matches any byte (optional) |
| Delay after connect | Wait before sending, while the headphones set up the audio link |
| Attempts | How many times to try before giving up |

Bytes are written in hex, e.g. `AA EC 09 00 01 01 A1`. Enter the full packets
exactly as captured, checksums included: the app does not need to understand
the protocol.

With the state replies filled in, the app confirms that game mode is really on
and retries otherwise. Without them, a command counts as done once it is sent.

**Test ON**, **Test OFF** and **Read state** try the values in the form before
you save them and show the bytes sent and received. **Start the procedure to
detect new headsets** starts the [guided detection](#option-a-guided-detection).
**Import** and **Export** load and save a profile as JSON.

## Profiles

Ready-made profiles are in [`profiles/`](profiles/). They are built into the
exe: pick yours from **Preset** in the advanced device settings, no download
needed. Currently supported headphones:

| headphones | file | provided by |
|---|---|---|
| Edifier X2s | [`edifier-x2s.json`](profiles/edifier-x2s.json) | Daniele Vergini |
| Realme Buds T200 Lite | [`realme-buds-t200-lite.json`](profiles/realme-buds-t200-lite.json) | Daniele Vergini |

Got it working with other headphones? Press **Send profile via GitHub PR** in the advanced device
settings: GitHub opens a Pull Request with the file ready, so you only have to confirm (you need a GitHub account). It will be offered as a preset from the next release.

## How to add support for other headphones

The idea: record the Bluetooth traffic while the official app toggles game mode,
then find the bytes that change. Either way you need an Android phone with the
headphones' companion app installed and a USB cable.

- **[Option A: guided detection (experimental)](#option-a-guided-detection)**: the app walks you
  through it, finds the commands in the log and tries them.
- **[Option B: manual procedure](#option-b-manual-procedure)**: you go through the
  log yourself with Wireshark, for full control or when option A finds nothing.

### Option A: guided detection

Open **Advanced device settings** and press **Start the procedure to detect new
headsets**. The list shows the paired headphones with their state, whether a
profile is present and their connection type. **Start detection procedure**
appears next to the ones that are connected and need detection.

The app then takes you through these steps:

1. **Android tools**: downloads ADB from Google.
2. **Phone**: enable USB debugging and checks the phone is connected with a USB cable.
3. **Bluetooth log**: turn on HCI snoop log and checks is really running.
4. **Headphones**: instruct to move them from the PC to the phone.
5. **Recording**: the app tells you when to change game mode in the companion app.
6. **Analysis**: gets the phone's bug report, reads the Bluetooth log then deletes the
   report and puts the log setting back.
7. **Test on the PC**: tries the commands on the headphones on PC and fills in the new custom profile, if it's working you press **Save**.

### Option B: manual procedure

You also need [adb](https://developer.android.com/tools/releases/platform-tools)
and [Wireshark](https://www.wireshark.org/) on the PC. You can use agentic AI
(like Claude Code) to do this procedure for you.

#### 1. Turn on the Bluetooth log on the phone

1. **Settings → About phone**, tap **Build number** 7 times to unlock the
   developer options.
2. In **Developer options** turn on **USB debugging** and set
   **Bluetooth HCI snoop log** to **Enabled** (not "Filtered", which strips the
   payloads).
3. Turn Bluetooth off and on again: the log starts with the new session.

#### 2. Record the toggle

1. Connect the headphones to the phone and open the companion app.
2. Wait a few seconds, then turn game mode **on and off 3 times**, about 5
   seconds apart. Change nothing else, so the log stays clean.
3. Note the start and end time on the phone's clock.

#### 3. Get the log

Connect the phone over USB, accept the "Allow USB debugging?" prompt, then:

```
adb bugreport bugreport.zip
```

Open the zip and extract `btsnoop_hci.log`. On most phones it is in
`FS/data/misc/bluetooth/logs/`; if not, search the zip for `btsnoop`
(e.g. Samsung uses `FS/data/log/bt/`).

Remember to set the snoop log back to **Disabled** when you are done.

#### 4. Find the commands

Open `btsnoop_hci.log` in Wireshark and apply this display filter, which shows
the data sent by the phone over RFCOMM:

```
btrfcomm && (btspp.data || data.data) && hci_h4.direction == 0x00
```

Use `hci_h4.direction == 0x01` instead to see what the headphones send back.
To get a plain list instead, use tshark (it comes with Wireshark):

```
tshark -r btsnoop_hci.log -Y "btrfcomm && (btspp.data || data.data)" -T fields -e frame.time -e hci_h4.direction -e btrfcomm.channel -e btspp.data -e data.data
```

Around the times you noted, look for **two packets that alternate 3 times each
and differ only in a byte or two**: those are ON and OFF. The RFCOMM layer of
the packet shows the **channel**. Right after each command, the headphones
usually answer with a confirmation or with their new state: that gives you the
state replies. Packets the app sends once at startup are often state queries.

If no packet repeats exactly, look for one byte that grows by one with every
message the app sends: it is a sequence number, and the ON/OFF packets are the
ones that are equal apart from it. Many headphones only echo it back, so a fixed
value works (try it with **Test ON**); the Realme example below is one of them.

If nothing shows up in RFCOMM but you see ATT writes
(`btatt.opcode == 0x12 || btatt.opcode == 0x52`), the headphones use BLE GATT,
which this app does not support yet.

#### 5. Try them with a custom profile

Open **Advanced device settings** from the tray icon, pick the headphones, enter the channel and
the bytes, then use **Test ON** / **Test OFF**. Turn off the phone's Bluetooth
first: if the phone is still connected, the companion app may keep the channel
busy. When it works, **Save**.

### Example 1: Edifier X2s strings

The Edifier Connect app uses RFCOMM channel 2 with this framing:

```
AA EC <cmd> <data length, 2 bytes BE> <data> <checksum>
```

- `AA` = host -> headphones, `BB` = headphones -> host
- checksum = sum of all preceding bytes, keeping the lowest byte

| command | bytes |
|---|---|
| game mode ON | `AA EC 09 00 01 01 A1` |
| game mode OFF | `AA EC 09 00 01 00 A0` |
| read state | `AA EC 08 00 00 9E` → reply `BB EC 08 00 02 <01/00> 00 <chk>` |

Other Edifier models may use the same framing with different command codes.

### Example 2: Realme Buds T200 Lite strings

The realme Link app uses RFCOMM channel 1 (SPP) with this framing, and no
checksum:

```
AA <length of the rest> <direction, 2 bytes> <cmd> <type> <seq> <data length, 2 bytes LE> <data>
```

- direction `00 00` = host -> headphones, `04 00` = headphones -> host
- `seq` grows by one with every message from the app, but the headphones only
  echo it back: the profile always sends `00`
- setting `06` is game mode; the app writes it with cmd `03` type `04` and
  reads settings with cmd `0D` type `01`

| command | bytes |
|---|---|
| game mode ON | `AA 09 00 00 03 04 00 02 00 06 01` |
| game mode OFF | `AA 09 00 00 03 04 00 02 00 06 00` |
| read state | `AA 09 00 00 0D 01 00 02 00 01 06` → reply `AA 0B 04 00 0D 81 <seq> 04 00 00 01 06 <01/00>` |

After a write the headphones also send `AA 0A 04 00 03 05 <seq> 03 00 01 06 <01/00>`
on their own.

## Build the application yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

`publish.ps1` builds `dist\GameModeTray.exe` (single file). Options:

## Disclaimer

This is an unofficial project, not affiliated with or endorsed by Edifier, Realme or
any other headphone maker. Trademarks belong to their respective owners. The
protocols were worked out by observing the Bluetooth traffic of the official
apps; use it at your own risk.

## License

[MIT](LICENSE)
