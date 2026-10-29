# Eliza Access

Eliza Access is a mod that makes Eliza, the visual novel by Zachtronics, playable with a screen reader. It reads dialogue, menus, the phone and its apps, the Eliza therapy sessions, and the game's minigames and end screens. It also describes the backgrounds and characters on screen. If your screen reader has a braille display connected, everything the mod reads goes to braille too.

It works with NVDA, JAWS and other Windows screen readers, and falls back to Windows' built-in voices if no screen reader is running. You can play with the keyboard or a controller.

- Join the Discord to chat, get help or report problems: <https://discord.gg/KNDXhztGGK>
- Support Shaysters on Ko-fi: <https://ko-fi.com/shaysters>

## Requirements

- Eliza for Windows, from Steam: <https://store.steampowered.com/app/716500/Eliza/>
- A screen reader such as NVDA or JAWS (optional; without one, the mod uses Windows' built-in speech).

## Installing

1. Close Eliza if it's running.
2. Download **ElizaAccessInstaller.exe** from the [latest release](https://github.com/shaysters/Eliza-Access/releases/latest).
3. Run it. It finds your Steam copy of Eliza by itself. If it can't, use the Browse button next to the game folder and choose the folder that contains `Eliza.exe`.
4. Choose **Install**.
5. Start Eliza from Steam as usual. The mod starts speaking at the title menu.

If the installer won't start and mentions `VCRUNTIME140.dll`, install the [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) and try again.

### Updating and uninstalling

Keep the installer. Run it again to update to the newest version, or to uninstall. Close the game first. The mod tells you on the title screen when an update is available.

### Installing by hand

If you'd rather not use the installer, download **ElizaAccess.zip** from the same release and extract it into the game folder, the one that contains `Eliza.exe`. On a default Steam install that's:

```
C:\Program Files (x86)\Steam\steamapps\common\Eliza
```

To uninstall a manual install, delete these files from the game folder: `ElizaAccessDll.dll`, `ElizaAccessBootstrap.dll`, `0Harmony.dll`, `prism.dll`, `Eliza.exe.config` and `ElizaAccess-README.md`. The mod doesn't change any of the game's own files.

### Upgrading from a very old version

Versions before 0.9.0 were installed by replacing the game's `libvorbisfile.dll`. If you used one of those, put the original file back first: in Steam, right-click Eliza, choose **Properties**, then **Installed Files**, then **Verify integrity of game files**. Then install as above. You can delete the leftover `libvorbisfile_orig.dll`.

## Keys

The mod turns on the game's controller mode, so the keyboard and a controller work the same way. Where a key has no controller button, the Controller column says "None".

| Keyboard | Controller | Where | What it does |
|---|---|---|---|
| Arrow keys | D-pad or left stick | Everywhere | Move between options. |
| Enter or Space | A | Everywhere | Select, or continue to the next line. |
| Escape | B | Everywhere | Go back, or open the pause menu. |
| Any key | Any button | Everywhere | Stop the current speech. |
| R | None | Everywhere | Repeat the last line of story: dialogue, Evelyn's thoughts, or a look-around description. |
| F5 | Back (View) | Everywhere | Open or close the mod's settings. |
| J | Right stick left | Scenes | Previous image: the background, then each character from left to right. |
| L | Right stick right | Scenes | Next image. Reads the name and position, then the full description. |
| K | Right stick up | Scenes | Read the current image's description again. |
| A | Right bumper | Story | Turn the game's auto-advance on or off. |
| Up and Down | D-pad or left stick | Look-around | Move between the things you can look at. Enter or A looks at one. |
| Tab | Y | Look-around | Continue the story. |
| Up and Down | D-pad or left stick | Emails, text conversations, Skandha messages | Read line by line. |
| Home and End | None | Emails, text conversations, Skandha messages | Jump to the first or last line. |
| 1, 2, 3 | X, left bumper, right bumper | Eliza app | Switch between the Profile, Inbox and Sessions tabs. |
| Arrow keys | D-pad or left stick | Eliza app profile | Move around the grid of badges. |
| F8 | Right stick click | Eliza sessions | Turn the emotion meter readout ("Sentiment analyzer: …") on or off. |
| Q | None | Eliza sessions | When reviewing Eliza's conclusions about a client, read the three conclusions and whether you agreed with each. |
| Home and End | None | Chapter select | Jump to the first or last chapter. |
| Up and Down | D-pad or left stick | History (the game's log of past dialogue) | Read one line at a time. |
| Up and Down | D-pad or left stick | Solitaire | Move between the main piles and the free cells. Left and Right move along a row. |

Notes:

- The mod announces when the location changes and when characters arrive or leave. In a text conversation on the phone, J, K and L describe the two people's profile pictures instead of the scene.
- Positions (left, centre, right) are approximate. They say which third of the screen the character is in.
- In a text conversation, the cursor starts on the newest message.
- The headset's other messages during Eliza sessions can be turned on or off in the settings, under **Eliza sessions**.

## Settings

Press **F5**, or **Back** (View) on a controller, anywhere in the game. Use Up and Down to move. Enter, Space or A turns a setting on or off, or opens a group. Escape or B goes back, and closes the menu from the top level. F5 or Back also closes it. Changes are saved immediately.

- **Read voiced dialogue aloud** (off by default): also read the lines that have voice acting, including what Evelyn says after you pick a reply. Turn this on if you use braille, or if you're playing in a language other than English.
- **Announce available updates at launch** (on by default): when a newer version of the mod is out, say so on the title screen.
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

## Credits

This project was developed by [Shaysters](https://github.com/shaysters) with assistance from [Bradley Renshaw](https://github.com/bradjrenshaw). A lot of the code, and the descriptions of the scenes and characters, were written with Claude Code, Anthropic's AI coding assistant.

Eliza is made by Zachtronics. This mod isn't affiliated with or endorsed by Zachtronics.

## For developers

Build instructions and how the mod works are in [DEVELOPING.md](https://github.com/shaysters/Eliza-Access/blob/main/DEVELOPING.md).
