# KH 1.5+2.5 Hotfix

Unofficial fix-up for **KINGDOM HEARTS -HD 1.5+2.5 ReMIX-** on Steam (Windows). It protects your saves from
the port's crashes, removes the known crash triggers, fixes the "Quit Game" freeze and releases KH1's endless
loading screens.
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
The Start menu folder **KH Hotfix** contains *Report a KINGDOM HEARTS bug*, *Restore a KINGDOM HEARTS save*,
*KH Hotfix diagnostics*, *Uninstall KH Hotfix* and the hotfix folder.

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
| You found a bug (glitch, freeze, wrong sound, anything odd) | Press **Ctrl+Alt+B** while it is on screen, describe it and click *Send report*. See [Reporting bugs](#reporting-bugs). |
| No sound any more (often after Bluetooth headphones dropped out) | The game cannot get its sound back once its audio output disappeared. Save, then restart the game. To prevent it, keep a second audio output enabled: see [Sound and endless loading screens](#sound-and-endless-loading-screens). |
| A loading screen or the end of a cutscene never finishes (KH1, the game keeps running) | Wait: the hotfix releases it, a loading screen at once and a cutscene within a few seconds when the game has lost its sound (see the row above), otherwise a loading screen after 10 seconds. If it is still stuck after 2 minutes, the cause is a different one: a snapshot was saved, please report it like a crash. |
| The game crashed | Your saves were backed up. With [Discord reporting](#reporting-bugs) set up, the crash report was sent automatically. Otherwise `Reports\<date>\summary.txt` in the hotfix folder contains a crash analysis; please share it in a [crash report issue](https://github.com/Gasteren/KH-Hotfix/issues/new?template=crash-report.md) so the crash can be fixed in code. |
| The installer says Windows blocked the program | Smart App Control or an antivirus blocks unsigned programs. The hotfix is compiled on your PC from the source code in `src\`; allow it in your antivirus or don't use the hotfix. |
| The game closes immediately (code `0x00000035`) | The game is shared through Steam Families and another family member is playing it. |

Logs are in `Logs\` and crash reports in `Reports\` inside the hotfix folder.

## What is wrong with the game

| Problem | Root cause |
|---|---|
| Random crashes (pause menu, loading screens, combat) | The engine is unstable above 60 FPS, and on high-refresh monitors it runs far above 60 by default. |
| Crashes on Alt+Tab | Exclusive-fullscreen swap-chain handling. |
| Freeze or crash when choosing *Quit Game* | **Fixed in code by the hotfix.** After the main loop ends, the engine's application destructor frees a buffer that was already released (access violation in `_aligned_free`) or deadlocks. Found by analyzing a real crash dump. |
| Endless loading screen in KH1 (the game keeps running, the loading icon spins forever), cutscenes that freeze at their end | **Released by the hotfix.** Both follow a failed sound output (next row): no sound ever "finishes" again, but before an area's character sound banks are loaded the game waits until its four character voice slots are idle, and cutscenes wait for their voices and sound streams to end. Found by analyzing a game stuck this way. |
| No sound for the rest of the session | The games output sound through XAudio2 on the Windows default device. When the last enabled audio output disappears, even for a moment (Bluetooth dropout, headset switched off, monitor asleep), XAudio2 reports a critical error; the games only record it and never rebuild their sound. |
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
6. **Endless loading screens and frozen cutscenes released (KH1)**: when an area load is blocked by a character voice
   slot whose sound never finishes, the hotfix marks that slot idle in the game's memory and the load continues at once.
   After a sound failure (see below) this happens within a quarter of a second, and sounds that stay "playing" for 5
   seconds are marked finished - the state the game itself sets when a sound ends - so cutscenes and events continue
   too. Without a sound failure only a load blocked for 10 seconds is released. The hotfix only acts in exactly these
   states, and only when the game's code matches the known version. A load stuck for another reason is snapshotted
   after 2 minutes for analysis.
7. **Sound failure alert**: when KH1's sound output fails, a Windows notification tells you to save and restart the
   game, and before launch the hotfix warns once when only one audio output is enabled (see below).
8. **Freeze watchdog** (fallback): a game process that closed its window but never exits is closed after 20 seconds; a
   window that stays *Not responding* for 2 minutes is closed too. Saves are backed up first.
9. **Panic hotkey Ctrl+Alt+K**: backs up the saves and closes a frozen game, even in fullscreen.
10. **Crash reports with analysis**: decoded exit codes, the game's own crash dump with an automatic call-stack analysis
    and crash signature (no debugger needed), Windows Error Reporting data and event-log entries in `Reports\<date>\`.
11. **Bug reports with one hotkey (Ctrl+Alt+B)**, and crash reports sent to Discord automatically when that is set up
    (see [Reporting bugs](#reporting-bugs)).
12. **Never blocks playing**: if the hotfix itself fails, the game is started normally.

## Playing above 60 FPS and helping fix the remaining crashes

The 60 FPS / 60 Hz limits are workarounds: the game still has crashes at high frame rates and on Alt+Tab in exclusive
fullscreen, and their cause is not known yet. Each one is fixable in the same way as the Quit Game crash once its crash
location is known, and the hotfix now records exactly that. To play without the limits and help:

1. In `KHHotfix.ini` (Start menu > KH Hotfix > KH Hotfix folder) set `TargetRefreshHz=0`.
2. Run `Install.cmd -Fps 0` from that folder (removes the NVIDIA 60 FPS cap).
3. Pick any settings in the game. Your saves stay protected by the backups and auto-repair.
4. When the game crashes, the crash report is posted automatically if [Discord reporting](#reporting-bugs) is set up.
   Otherwise open a [crash report issue](https://github.com/Gasteren/KH-Hotfix/issues/new?template=crash-report.md)
   and paste `Reports\<date>\summary.txt`. Crashes with the same *Signature* line are the same bug.

To go back to the safe limits, set `TargetRefreshHz=60` again and run `Install.cmd`.

## Sound and endless loading screens

The KH games play their sound on the Windows default audio output. When that output disappears and another one is
enabled, Windows moves the game's sound there (and back when the first one returns). But when the **only** enabled
output disappears, even for a second - Bluetooth headphones dropping out or switching off, a USB headset being
unplugged, a monitor with speakers going to sleep - the game's sound engine fails for good: no sound until the game
is restarted, and in KH1 loading screens and cutscene endings wait forever for sounds that never finish.

- **Prevent it:** keep a second audio output enabled. Windows Settings > System > Sound > *All sound devices*: pick
  your monitor, speakers or any other output and choose *Allow*; keep your usual device selected as the default output.
  The hotfix warns once before launch when only one output is enabled (`KHHotfixCli.exe --audio-devices` lists them).
- **When it happens:** the hotfix shows a notification, keeps KH1's loading screens and cutscenes going, and tells you
  in the session summary. Save at the next save point, then restart the game to get the sound back.

## Reporting bugs

Press **Ctrl+Alt+B** while playing, or use Start menu > KH Hotfix > *Report a KINGDOM HEARTS bug*. The hotfix captures
the moment right away: a screenshot of the game, what the game was doing (for KH1 its loading and sound state) and a
technical snapshot. Then a window opens where you pick the game and the kind of problem and describe what happened.
*Send report* posts it to the Discord server of whoever gave you the hotfix, if they set that up. Otherwise *Save report*
keeps it in `Reports\bug-<date>\` for a GitHub issue. In exclusive fullscreen the window opens once you leave the game,
because switching windows there can crash KH; Borderless Windowed avoids that.

Crashes, freezes and sessions in which the hotfix had to step in (lost sound, released loading screens, a deleted save)
are posted automatically after the session, with the logs and the crash analysis. This also only happens when Discord
reporting is set up.

Players only get messages that need them: a save that was damaged, restored or deleted, saving that is blocked, lost
sound. What the hotfix handled by itself (released loading screens, closed frozen games, crash reports sent to Discord)
is only logged and sent with these reports; `DetailedMessages=1` in `KHHotfix.local.ini` shows it too. Without Discord
reporting, crash notes are shown, because then the player has to share the report.

**What is sent:** your description, the screenshot and the session log. It also includes system information: Windows,
CPU, GPU and driver, RAM, display, audio outputs, game build and hotfix settings. If ticked, the technical snapshot is
sent too. Your Windows user name, PC name and Steam ID are removed from the text, and reports from the same PC carry an
anonymous tag such as `PC-1A2B`. The technical snapshot (a minidump: part of the game's memory) is sent as it is; untick
it if you prefer.

### Setting up Discord reporting (for whoever shares the hotfix)

1. In your Discord server create a **forum channel** for bug reports (each report becomes its own post) and a channel
   for the automatic crash reports (forum or text channel). In each: *Edit Channel* > *Integrations* > *Webhooks* >
   *New Webhook* > *Copy Webhook URL*.
2. Create `KHHotfix.local.ini` in the hotfix folder:
   ```ini
   DiscordBugReportWebhook=https://discord.com/api/webhooks/...
   DiscordLogWebhook=https://discord.com/api/webhooks/...
   ```
   `KHHotfixCli.exe --discord-test` checks both webhooks; `--discord-test --send` posts a test message to each.
3. Send that `KHHotfix.local.ini` **privately** to your players. They put it next to `Install.cmd` in the extracted
   download before running it. The installer adds the settings to their installation; on later updates their own
   settings stay.

**Keep webhook URLs private.** Anyone with the URL can post to that channel and delete the webhook. Never put them in
`KHHotfix.ini` or on GitHub; `KHHotfix.local.ini` is ignored by git. If one leaks, delete the webhook in Discord and
create a new one. If the forum requires tags, turn that off or put the tag ids in `DiscordBugReportTags`.

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
display), watchdog timings, panic hotkey, dialogs (`ShowDialogs`; `DetailedMessages=1` also shows what the hotfix
handled by itself), extra game arguments (for example `-reboot=true` to skip the logo videos), `FixQuitCrash` (the
in-memory Quit Game fix, on by default) and `LoadingRescueSeconds` (how long a
KH1 area load may be blocked by a stuck voice before it is released, and whether stuck sounds are finished after a
sound failure; `0` = only report, never change the game). Also `BugReportHotkey` (default `Ctrl+Alt+B`, empty = off)
and the Discord reporting settings `DiscordBugReportWebhook`, `DiscordLogWebhook` and `DiscordBugReportTags`, which
belong in `KHHotfix.local.ini` (see [Reporting bugs](#reporting-bugs)). Settings in `KHHotfix.local.ini` override
`KHHotfix.ini`.

## For developers

- `Install.cmd` builds `KHHotfix.exe` (Steam wrapper, no console) and `KHHotfixCli.exe` (maintenance commands) from
  `src\*.cs` with the C# compiler included in Windows (.NET Framework 4.x); `build.cmd` does the same manually.
  When run from a git clone the hotfix stays in place instead of moving to `%LOCALAPPDATA%`. Put personal settings in
  `KHHotfix.local.ini` there: it overrides `KHHotfix.ini` and is ignored by git.
- Tests: `KHHotfixCli.exe --selftest` (unit tests, including a local fake Discord), `tests\Run-Tests.ps1` (end-to-end
  with a fake game and a fake Discord: crash, quit freeze, frozen window, save corruption, deleted save, automatic and
  command-line reports; plus read-only checks against the installed game), `tests\Test-SteamConfig.ps1` (Steam config editing),
  `tests\Test-Install.ps1` (fresh install from a downloaded ZIP, update, uninstall).
- CLI: `--check`, `--backup`, `--restore-backup <gz> <save>`, `--display-info`, `--restore-display`, `--nv-status`,
  `--nv-apply [--fps N] [--no-vsync] [--no-gsync-off]`, `--nv-revert`, `--fixes-scan <game folder>`,
  `--fixes-apply <pid> [--exit-code <marker>]`, `--loading-status <pid>`, `--loading-rescue <pid>`, `--audio-devices`,
  `--report-bug [folder]` (report window), `--report-bug [folder] --send --title <t> --text <d> [--game KH1] [--category Sound]
  [--name <n>] [--pid <game pid>] [--no-screenshot] [--no-logs] [--no-snapshot]`, `--discord-test [--send]`,
  `--analyze-dump <file.dmp> [game folder]`, `--paths`, `--version`.
- Code fixes live in `src\GameFixes.cs`: each one is a byte signature (must match exactly once in the executable) plus
  the replacement code. `--fixes-apply <pid> --exit-code 0x4B51` makes the quit fix exit with a marker code, which
  proves on a live game that the patched path runs.
- The endless-loading guard lives in `src\LoadingGuard.cs`. It finds the voice slots, the sound-bank task list, the
  area-load flag, the sound library's audio-failure flag (set by its XAudio2 `OnCriticalError` callback, never read by
  the game) and the voice pool through the code that uses them. `--loading-status <pid>` shows what a running KH1 is
  waiting for; `--loading-rescue <pid>` releases a stuck load immediately. `src\AudioDevices.cs` lists the audio
  outputs through the Windows Core Audio API.

Sources: PCGamingWiki (Kingdom Hearts HD 1.5 + 2.5 ReMIX), Steam Community discussions for app 2552430,
the KH-ReFined feature list, NVIDIA's public NVAPI headers.

## License

[MIT](LICENSE)
