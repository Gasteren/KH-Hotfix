# KH 1.5+2.5 Hotfix

Fixes for **KINGDOM HEARTS -HD 1.5+2.5 ReMIX-** on Steam (Windows 10/11).
Game files are not changed, and the uninstaller reverts everything.

> Unofficial - not affiliated with Square Enix or Disney. Use at your own risk.

## What it fixes

- **Endless loading screens and cutscenes that never end** (KH1).
- **The "Quit Game" crash and freeze** (all games).
- **Saves lost to crashes** ("Save data is corrupt"): the damaged save is replaced with your last good one before the
  game can delete it.
- **No 60 FPS limit needed:** play at any frame rate and refresh rate.

## What it does

- Backs up your saves every time the game saves.
- Tells you when saving is blocked ("Failed to save game data") and why.
- Tells you when the game lost its sound because your headphones or speakers disconnected (restart the game to get
  it back).
- Closes a frozen game after backing up your saves.
- Sends crash reports and your bug reports to the developer, so problems get fixed (logs and system info; your
  Windows user name, PC name and Steam ID are removed). `AutoReports=0` in `KHHotfix.local.ini` stops the automatic ones.
- **Ctrl+Alt+B** reports a bug with a screenshot. **Ctrl+Alt+K** backs up your saves and closes a frozen game.

## Install

1. Download **[KH-Hotfix-main.zip](https://github.com/Gasteren/KH-Hotfix/archive/refs/heads/main.zip)** and extract
   it (right-click > *Extract All*).
2. Run **`Install.cmd`**. If Windows warns you, click *More info* > *Run anyway*. Press Enter when it asks to restart
   Steam.
3. Start the game from Steam as usual.

To update, do the same with a new download.

## Uninstall

Run **`Uninstall.cmd`**, or Start menu > **KH Hotfix** > *Uninstall KH Hotfix*. Your saves are not touched.

## Problems?

- **"Save data is corrupt":** Start menu > KH Hotfix > *Restore a KINGDOM HEARTS save*.
- **Crash on Alt+Tab:** set *Screen Mode* to *Borderless Windowed* in the KH launcher's System Settings.
- **Anything else:** press **Ctrl+Alt+B** while it happens, or use Start menu > KH Hotfix > *Report a KINGDOM HEARTS bug*.

## License

[MIT](LICENSE)
