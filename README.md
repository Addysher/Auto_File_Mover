# Auto File Mover 2.0 (C#)

Moves saw output files from a drop folder into one folder per job.

By default, the job name is **read from inside each file**: it is the second field on the MiTek `M1` header line.
For example, `12345ABC__STR-01.mme` contains `M1 12345ABC Stringer â€¦`, so it goes to `Target\12345ABC\`.
Files with no matching header are left in place and logged.
Alternatively, you can use the old file-name rule: the name without its extension and without its last *N* characters (default 3).
You can also use the file-name rule only as a fallback for files that have no header.

A rewrite of the original Python/Tkinter script. There are two ways to run it, and both use the same engine:

| | **Tray app** `AutoFileMover.exe` | **Windows service** `AutoFileMover.Service.exe` |
|---|---|---|
| Runs as | the logged-in user | LocalSystem (or an account you choose) |
| Starts | at login ("Start with Windows" in the tray menu) | at boot, nobody needs to log in |
| Can use OneDrive / mapped drives | yes | no, so use local or UNC paths (`\\server\share\...`) |
| Settings UI | yes | edit with the tray app or `settings.json` |

**If the service is installed, the tray app moves nothing itself.** It becomes a control panel for the service (settings, pause, run now, start/stop, live log).
If the service is not installed, the tray app moves the files itself. A lock file makes sure two copies never run at the same time.

## What changed from the Python version

- **Fixes the "-1 seconds" freeze.** The old copy loop ran on the GUI thread and only stopped when the source folder was completely empty. A locked file, a leftover subfolder, or a job folder that already existed kept it spinning forever. An unexpected error stopped the countdown for good. Now each scan looks at every file once, retries anything stuck later, and logs errors instead of dying.
- **Reacts in seconds.** New files are picked up as soon as they arrive, instead of after a 5-minute countdown. A full sweep still runs every 300 s as a backup.
- **Never moves half-written files.** A file must stay unchanged, and not be open in another program, for *settle time* seconds (default 5) before it moves.
- **New job folders appear complete.** They are built in a hidden `.automover-staging` folder inside the target, then moved into place in one step. A folder left behind by a crash or power cut is finished on the next scan.
- Same-name files in a job folder are **overwritten**.
- Job names are read from the file header, not guessed from the file name. With the old rule, `12345ABC__STR-01` would have become `12345ABC__STR`.
- Files without a usable job name are left in place and logged. They no longer crash the app.
- Hidden and system files (`desktop.ini`, `Thumbs.db`) are ignored.
- Writes the daily log `Saw_Mover-yyyy-MM-dd.log`, kept for 30 days.
- Ships as a normal .NET exe instead of a PyInstaller bundle, so antivirus false alarms are much less likely.

## Jobs tab: moving job folders onward

The tray app's **Jobs** tab shows two lists side by side:
- **Target (jobs ready)**: the job folders the automatic mover has built (DataOut).
- **Send-to folder**: where jobs go next, e.g. the saw queue. Set it on the **Settings** tab.

To move whole job folders, select them (Ctrl/Shift-click for several, Ctrl+A for all) and click **→** or **←**, or drag them from one list to the other.
- On the same drive a move is an instant rename. Across drives or to a server, the files are copied and the originals deleted.
- If a job already exists on the other side, the app asks before merging. Same-named files are overwritten.
- Files that are open in another program stay where they are and are reported. Move the job again later to finish.

Both lists refresh on their own. Click a column header to sort, use the filter box to find a job, and double-click a job to open it in Explorer. Every move is written to the daily log.
This works the same whether the files are moved by the tray app or by the Windows service, because the Jobs tab always runs as the logged-in user.

## Files

```
%ProgramData%\AutoFileMover\
    settings.json      â† shared by service + tray app
    status.json        â† live status written by the service
    Logs\Saw_Mover-2026-11-09.log
```

`settings.json`:

```json
{
  "SourceFolder": "C:\\Users\\Me\\OneDrive\\Documents\\MiTek\\DataIn",
  "TargetFolder": "C:\\Users\\Me\\OneDrive\\Documents\\MiTek\\DataOut",
  "SendToFolder": "\\\\saw-pc\\SawQueue",
  "JobNameSource": "FileContent",
  "ContentPattern": "^M1\\s+(?<job>\\S+)",
  "FallbackToFileName": false,
  "CharactersToStrip": 3,
  "SettleSeconds": 5,
  "SweepIntervalSeconds": 300,
  "StageNewFolders": true,
  "Paused": false,
  "LogRetentionDays": 30
}
```

Changes to this file are picked up automatically. Nothing needs restarting.

`JobNameSource` is either `FileContent` or `FileName`.
`ContentPattern` is a regular expression, matched line by line over the first 64 KB of the file. Its `job` group (or its first group) becomes the folder name.
In the settings window, **Test a fileâ€¦** shows which folder a real file would go to before you save.
On first start, the tray app imports `source_folder` / `target_folder` from an old `settings.ini` if it finds one next to the exe.

## Build

Requires the .NET 10 SDK (pinned in global.json).

```powershell
.\publish.ps1          # runs tests, then builds self-contained single-file exes into .\publish
```

## Install: tray app only

1. Copy `publish\AutoFileMover.exe` anywhere, e.g. `C:\Program Files\AutoFileMover\`.
2. Run it, set the folders, click **Save**.
3. In the tray menu, tick **Start with Windows**.

## Install: Windows service

1. Copy the `publish\Service` folder to the PC, e.g. `C:\Program Files\AutoFileMover\Service`.
2. In an **elevated** PowerShell in that folder, run:
   `powershell -ExecutionPolicy Bypass -File .\install-service.ps1`
   This installs the service as *Automatic (Delayed Start)*, sets it to restart if it fails, and lets normal users edit the settings.
3. Set the folders with the tray app (or edit `settings.json`). Use local or UNC paths, not `Z:\` or OneDrive.
4. Optional: to reach network shares that need credentials, open `services.msc`, find **Auto File Mover**, and set a domain or local account on the **Log On** tab.

To remove it: run `uninstall-service.ps1` (elevated). Settings and logs are kept.

Errors from the service are also written to the Windows Event Log (Application).

