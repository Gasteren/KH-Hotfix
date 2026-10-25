# KH 1.5+2.5 Hotfix

Unofficial fix-up for **KINGDOM HEARTS -HD 1.5+2.5 ReMIX-** on Steam (Windows). It protects your saves from
the port's crashes, removes the known crash triggers and fixes the "Quit Game" freeze.
No game files are changed, no administrator rights are needed, and the uninstaller reverts everything.

> Not affiliated with or endorsed by Square Enix or Disney. No game files are included. Use at your own risk.

## Install

1. Download **[KH-Hotfix-main.zip](https://github.com/Gasteren/KH-Hotfix/archive/refs/heads/main.zip)**.
2. Right-click the ZIP > **Properties** > tick **Unblock** (bottom right) > **OK**.
   This prevents "unknown publisher" warnings later.
3. Right-click the ZIP > **Extract All...** > **Extract**.
4. In the extracted folder, double-click **`Install.cmd`**.
   If Windows shows *"Windows protected your PC"*, click **More info** > **Run anyway**.
5. When it asks to restart Steam, press **Enter**. This is needed once to set the game's launch option.
6. Start the game from Steam as usual. In the KH launcher open **System Settings** and set
   **Screen Mode: Borderless Windowed**, **Frame Rate: 60**, **Refresh Rate: ON**.

The hotfix installs itself to `%LOCALAPPDATA%\KH-Hotfix`, so you can delete the downloaded files afterwards.
The Start menu folder **KH Hotfix** contains *Restore a KINGDOM HEARTS save*, *KH Hotfix diagnostics*,
*Uninstall KH Hotfix* and the hotfix folder.

- **Update:** download the ZIP again and run `Install.cmd`. Your settings and backups are kept.
- **Uninstall:** Start menu > KH Hotfix > *Uninstall KH Hotfix*.
- **Requirements:** Windows 10 or 11 (64-bit) and the Steam version of the game. Any graphics card works;
  the driver-level 60 FPS cap is NVIDIA-only, on other cards the in-game *Frame Rate: 60* setting does that job.

## Troubleshooting

| Problem | What to do |
|---|---|
| The game does not start any more | Start menu > KH Hotfix > *Uninstall KH Hotfix* (or clear the launch option in Steam > game > Properties > General), then run *KH Hotfix diagnostics*. |
| "Save data is corrupt" or lost progress | Quit the game, run *Restore a KINGDOM HEARTS save* and pick the newest version that is not marked *damaged*. |
| "Failed to save game data" | Run *KH Hotfix diagnostics*. If Controlled Folder Access (ransomware protection) is on, run `Install.cmd` again and answer **Y** when it offers to allow the game. |
| The game freezes | Press **Ctrl+Alt+K**: your saves are backed up and the game is closed. |
| The game crashed | Your saves were backed up. `Reports\<date>\summary.txt` in the hotfix folder contains a crash analysis; please share it in a [crash report issue](https://github.com/Gasteren/KH-Hotfix/issues/new?template=crash-report.md) so the crash can be fixed in code. |
| The installer says Windows blocked the program | Smart App Control or an antivirus blocks unsigned programs. The hotfix is compiled on your PC from the source code in `src\`; allow it in your antivirus or don't use the hotfix. |
| The game closes immediately (code `0x00000035`) | The game is shared through Steam Families and another family member is playing it. |

Logs are in `Logs\` and crash reports in `Reports\` inside the hotfix folder.

## What is wrong with the game

| Problem | Root cause |
|---|---|
| Random crashes (pause menu, loading screens, combat) | The engine is unstable above 60 FPS, and on high-refresh monitors it runs far above 60 by default. |
| Crashes on Alt+Tab | Exclusive-fullscreen swap-chain handling. |
| Freeze or crash when choosing *Quit Game* | **Fixed in code by the hotfix.** After the main loop ends, the engine's application destructor frees a buffer that was already released (access violation in `_aligned_free`) or deadlocks. Found by analyzing a real crash dump. |
| Lost or "corrupt" saves | Each game keeps all its slots in one file (`KHFM_WW.png`, `KHIIFM_WW.png`, ...) that is rewritten in place. A crash while saving leaves it cut off; the game then reports *"Save data is corrupt and will be deleted"* and deletes it. |
| *"Failed to save game data"* | The game finds the Documents folder through legacy ANSI Windows APIs, so non-ASCII profile paths, OneDrive, Controlled Folder Access, read-only files or a full disk make every save fail. |
| Stutter and uneven frame pacing | VRR (G-SYNC/FreeSync) and refresh rates above 60 Hz. |

Saves live in `Documents\My Games\KINGDOM HEARTS HD 1.5+2.5 ReMIX\Steam\<SteamID64>\` and are tied to the Steam account.

## What the hotfix does

Steam starts the game through `KHHotfix.exe` (launch option `"<hotfix folder>\KHHotfix.exe" %command%`). Every session:

1. **Save check** before the game starts: verifies that the game can really write its save folder (using the same
   path the game sees), clears read-only flags, warns about OneDrive, ransomware protection and low disk space.
2. **Versioned save backups** before launch, whenever the game saves, and after exit. Deduplicated and compressed;
   the newest 40 versions per file plus one per day for 30 days are kept in `Backups\`.
3. **Auto-repair**: a save damaged by a crash is replaced by the last good version *before* the game can delete it.
   The rules are calibrated against your own good saves, so an unexpected file is never touched. Damaged files
   are kept in `Backups\_quarantine\`.
4. **60 Hz while playing**: the main monitor runs at 60 Hz during the session and gets its normal refresh rate back
   afterwards, even after a crash.
5. **Quit Game crash/freeze fixed in code**: when a KH game starts, the hotfix replaces the buggy shutdown call in
   the game's memory with a clean exit that keeps the game's exit code (KH1, Re:CoM, KH2, BBS and Theater). The game
   files on disk are never changed, and the fix is only applied when the code matches the known game version
   byte for byte, so a game update can't be broken by it. Verified on all five games.
6. **Freeze watchdog** (fallback): a game process that closed its window but never exits is closed after 20 seconds; a
   window that stays *Not responding* for 2 minutes is closed too. Saves are backed up first.
7. **Panic hotkey Ctrl+Alt+K**: backs up the saves and closes a frozen game, even in fullscreen.
8. **Crash reports with analysis**: decoded exit codes, the game's own crash dump with an automatic call-stack analysis
   and crash signature (no debugger needed), Windows Error Reporting data and event-log entries in `Reports\<date>\`.
9. **Never blocks playing**: if the hotfix itself fails, the game is started normally.

## Playing above 60 FPS and helping fix the remaining crashes

The 60 FPS / 60 Hz limits are workarounds: the game still has crashes at high frame rates and on Alt+Tab in exclusive
fullscreen, and their cause is not known yet. Each one is fixable in the same way as the Quit Game crash once its crash
location is known, and the hotfix now records exactly that. To play without the limits and help:

1. In `KHHotfix.ini` (Start menu > KH Hotfix > KH Hotfix folder) set `TargetRefreshHz=0`.
2. Run `Install.cmd -Fps 0` from that folder (removes the NVIDIA 60 FPS cap).
3. Pick any settings in the game. Your saves stay protected by the backups and auto-repair.
4. When the game crashes, open a [crash report issue](https://github.com/Gasteren/KH-Hotfix/issues/new?template=crash-report.md)
   and paste `Reports\<date>\summary.txt`. Crashes with the same *Signature* line are the same bug.

To go back to the safe limits, set `TargetRefreshHz=60` again and run `Install.cmd`.

## Changes made to your PC

All of these are reverted by the uninstaller:

- The Steam launch option of this game (your other launch options are kept).
- NVIDIA only: the driver profile of the KH executables gets a 60 FPS limit, V-Sync on and G-SYNC off. Previous
  values are recorded and restored.
- Windows graphics preference *High performance* for the KH executables (matters on laptops with two GPUs).
- Start menu folder *KH Hotfix*.
- Only when you allow it (administrator prompt): Controlled Folder Access permission for the KH executables, and
  Windows crash dumps for them in `CrashDumps\`.

Your real save files are never deleted; the hotfix only adds backups.

## Settings

`KHHotfix.ini` in the hotfix folder: backup retention, auto-repair, target refresh rate (`0` = never touch the
display), watchdog timings, panic hotkey, dialogs, extra game arguments (for example `-reboot=true` to skip the
logo videos) and `FixQuitCrash` (the in-memory Quit Game fix, on by default).

## For developers

- `Install.cmd` builds `KHHotfix.exe` (Steam wrapper, no console) and `KHHotfixCli.exe` (maintenance commands) from
  `src\*.cs` with the C# compiler included in Windows (.NET Framework 4.x); `build.cmd` does the same manually.
  When run from a git clone the hotfix stays in place instead of moving to `%LOCALAPPDATA%`.
- Tests: `KHHotfixCli.exe --selftest` (unit tests), `tests\Run-Tests.ps1` (end-to-end with a fake game: crash,
  quit freeze, frozen window, save corruption, deleted save), `tests\Test-SteamConfig.ps1` (Steam config editing),
  `tests\Test-Install.ps1` (fresh install from a downloaded ZIP, update, uninstall).
- CLI: `--check`, `--backup`, `--restore-backup <gz> <save>`, `--display-info`, `--restore-display`, `--nv-status`,
  `--nv-apply [--fps N] [--no-vsync] [--no-gsync-off]`, `--nv-revert`, `--fixes-scan <game folder>`,
  `--fixes-apply <pid> [--exit-code <marker>]`, `--analyze-dump <file.dmp> [game folder]`, `--paths`, `--version`.
- Code fixes live in `src\GameFixes.cs`: each one is a byte signature (must match exactly once in the executable) plus
  the replacement code. `--fixes-apply <pid> --exit-code 0x4B51` makes the quit fix exit with a marker code, which
  proves on a live game that the patched path runs.

Sources: PCGamingWiki (Kingdom Hearts HD 1.5 + 2.5 ReMIX), Steam Community discussions for app 2552430,
the KH-ReFined feature list, NVIDIA's public NVAPI headers.

## License

[MIT](LICENSE)
