# Eliza Access

Eliza Access is a mod that makes Eliza, the visual novel by Zachtronics, playable with a screen reader. It reads dialogue, menus, the phone and its apps, the Eliza therapy sessions, and the game's minigames and end screens. It also describes the backgrounds and characters on screen. If your screen reader has a braille display connected, everything the mod reads goes to braille too.

It works with NVDA, JAWS and other Windows screen readers, and falls back to Windows' built-in voices if no screen reader is running. You can play with the keyboard or a controller.

## Requirements

- Eliza for Windows, from Steam: https://store.steampowered.com/app/716500/Eliza/
- A screen reader such as NVDA or JAWS (optional; without one, the mod uses Windows' built-in speech).

## Installing

1. Close Eliza if it's running.
2. Download **ElizaAccessInstaller.exe** from the [latest release](https://github.com/shaysters/Eliza-Access/releases/latest).
3. Run it. It finds your Steam copy of Eliza by itself. If it can't, use the Browse button next to the game folder and choose the folder that contains `Eliza.exe`.
4. Choose **Install**.
5. Start Eliza from Steam as usual. The mod starts speaking at the title menu.

If the installer won't start and mentions `VCRUNTIME140.dll`, install the [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) and try again.

### Updating and uninstalling

Keep the installer. Run it again to update to the newest version, or to uninstall. Close the game first.

### Installing by hand

If you'd rather not use the installer, download **ElizaAccess.zip** from the same release and extract it into the game folder, the one that contains `Eliza.exe`. On a default Steam install that's:

```
C:\Program Files (x86)\Steam\steamapps\common\Eliza
```

To uninstall a manual install, delete these files from the game folder: `ElizaAccessDll.dll`, `ElizaAccessBootstrap.dll`, `0Harmony.dll`, `prism.dll` and `Eliza.exe.config`. The mod doesn't change any of the game's own files.

### Upgrading from a very old version

Versions before 0.9.0 were installed by replacing the game's `libvorbisfile.dll`. If you used one of those, put the original file back first: in Steam, right-click Eliza, choose **Properties**, then **Installed Files**, then **Verify integrity of game files**. Then install as above. You can delete the leftover `libvorbisfile_orig.dll`.

## Keys

The mod turns on the game's controller mode, so the keyboard and a controller work the same way. Arrow keys move, Enter selects.

### Everywhere

| Key | Controller | What it does |
|---|---|---|
| Arrow keys | D-pad or left stick | Move between options. |
| Enter | A | Select, or continue to the next line. |
| Space | | Continue to the next line. |
| Escape | B | Go back, or open the pause menu. |
| R | | Repeat the last line of story: dialogue, Evelyn's thoughts, or a look-around description. |
| Any key | Any button | Stop the current speech. |
| F5 | Back (View) | Open or close the mod's settings. |

### Scenes and characters

| Key | Controller | What it does |
|---|---|---|
| J | Right stick left | Previous image: the background, then each character from left to right. |
| L | Right stick right | Next image. Reads the name and position, then the full description. |
| K | Right stick up | Read the current image's description again. |

The mod also announces when the location changes and when characters arrive or leave. In a text conversation on the phone, J, K and L describe the two people's profile pictures instead.

Positions (left, centre, right) are approximate. They say which third of the screen the character is in.

### Story

| Key | What it does |
|---|---|
| A | Turn the game's auto-advance on or off. This is the game's own key. |

In look-around scenes, use Up and Down to move between the things you can look at, and Enter to look at one.

### Phone apps

| Key | Where | What it does |
|---|---|---|
| Up and Down | Emails, text conversations, Skandha messages | Read line by line. |
| Home and End | Same places | Jump to the first or last line. |
| 1, 2, 3 | Eliza app | Switch between the Profile, Inbox and Sessions tabs. |
| Arrow keys | Eliza app profile | Move around the grid of badges. |

In a text conversation, the cursor starts on the newest message.

### Eliza sessions

| Key | Controller | What it does |
|---|---|---|
| F8 | Right stick click | Turn the emotion meter readout ("Sentiment analyzer: …") on or off. |
| Q | | When reviewing Eliza's conclusions about a client, read the three conclusions and whether you agreed with each. |

The headset's other messages can be turned on or off in the F5 settings, under **Eliza sessions**.

### Menus and minigames

| Key | Where | What it does |
|---|---|---|
| Home and End | Chapter select | Jump to the first or last chapter. |
| Up and Down | History (the game's log of past dialogue) | Read one line at a time. |
| Up and Down | Solitaire | Move between the main piles and the free cells. Left and Right move along a row. |

## Settings

Press **F5**, or **Back** (View) on a controller, anywhere in the game. Use Up and Down to move. Enter, Space or A turns a setting on or off, or opens a group. Escape or B goes back, and closes the menu from the top level. F5 or Back also closes it. Changes are saved immediately.

- **Read voiced dialogue aloud** (off by default): also read the lines that have voice acting, including what Evelyn says after you pick a reply. Turn this on if you use braille, or if you're playing in a language other than English.
- **Eliza sessions**: choose which of the headset's messages are read during a session:
  - Read bootup text (the headset's startup messages)
  - Read sentiment analyzer (the emotion meter; F8 toggles this too)
  - Read multimodal sense
  - Read phase changes
  - Read miscellaneous notifications and system events

The mod's menus and announcements follow the game's language setting. It's translated into all ten of the game's languages.

## Troubleshooting

The mod keeps its settings and a log in:

```
%LOCALAPPDATA%\ElizaAccess
```

You can paste that into the Run dialog (Windows+R) to open it. The log for the last play session is `Logs\eliza-access.log`; the one before it is `eliza-access.prev.log`. If something goes wrong, include the log when you report it.

- **No speech at all:** make sure the files are in the folder with `Eliza.exe`, and that the game was started after installing. If the mod failed to start, there may be a log at `%TEMP%\eliza-access-bootstrap.log`.
- **Speech stops when you switch windows:** this is on purpose. The mod goes quiet while the game isn't the active window.

To report a problem or suggest something, open an issue on the [GitHub page](https://github.com/shaysters/Eliza-Access/issues).

## For developers

Build instructions and how the mod works are in [DEVELOPING.md](DEVELOPING.md).
