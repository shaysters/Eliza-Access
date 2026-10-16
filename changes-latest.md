# Eliza Access 0.9.0

First release with an installer. Download **ElizaAccessInstaller.exe**, run it, and it finds your Steam copy of Eliza and installs the mod. Run it again later to update or uninstall. Close the game before installing.

If the installer won't start and mentions VCRUNTIME140.dll, install the Microsoft Visual C++ Redistributable (x64) from Microsoft and try again.

To install by hand instead, extract **ElizaAccess.zip** into the game folder (the one containing Eliza.exe).

## Upgrading from a manual install

Earlier versions were installed by replacing the game's `libvorbisfile.dll`. This version doesn't touch any game files. Before running the installer, open Steam, right-click Eliza, choose **Properties → Installed Files → Verify integrity of game files**. That puts the original `libvorbisfile.dll` back. You can delete the leftover `libvorbisfile_orig.dll` afterwards.

Your mod settings carry over automatically. They and the logs now live in `%LOCALAPPDATA%\ElizaAccess` instead of Documents.

## New in this version

- Scene and character descriptions: J and L step through the background and the characters on screen (or a conversation's avatars), K reads the full description. Location changes and characters arriving or leaving are announced.
- Emails, Skandha messages and text conversations can be read line by line with Up and Down; Home and End jump to the start and end. Home and End also work in chapter select.
- Controller support for look-around, the Eliza profile badges, Solitaire and History.
- The chapter title is announced at each new chapter.
- Dialogue reads speakers by first name only, and a silent "…" line is read as "ellipsis".
- The star rating after Evelyn's own Eliza session is now read in the right direction.
