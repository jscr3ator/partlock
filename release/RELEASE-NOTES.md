## What's new in v0.4.0
- **Checking out an assembly offers its sub-assemblies too.** PartLock asks SolidWorks which sub-assemblies the assembly uses (every level) and shows them ticked: **Check out ticked too**, **Just the assembly**, or **Cancel**. Sub-assemblies someone else has checked out are listed and skipped. Parts aren't included. SolidWorks needs to be open for this; if it isn't, only the assembly is checked out, same as before.
- **In-app updates.** When a new version is out, the bottom-right of PartLock says **"PartLock vX is available – click to update"**. Click it and PartLock downloads the new version, swaps itself and restarts. Your files and check-outs aren't touched. You can also check any time: **@you → Check for PartLock updates…**
- **Right-click in File Explorer.** Right-click any CAD file in the vault → **PartLock** → **Check Out**, **Check In…**, **Undo Check Out** or **Open in PartLock**. On Windows 11 it's under **Show more options** (or Shift+right-click). Turn it off or on from the 📁 repository button → **Right-click menu in File Explorer**.

> Updating from v0.3.0 or older: download PartLock.exe below once and replace your old one. From v0.4.0 on, PartLock updates itself.

### Earlier
- v0.3.0: GitHub account and repository switcher, Check In without Check Out, changes bar, file-name search, team logo.
- v0.2.0: installs Git for you, requires GitHub sign-in.

## Download
- **PartLock-v0.4.0.zip**: unzip anywhere and run `PartLock.exe`, or
- **PartLock.exe**: the app on its own.

## Requirements
- Windows 10/11 (.NET Framework 4.8 is built in)
- A GitHub account with access to your team's repository
- SolidWorks open, for the sub-assembly check-out

Please report problems with the contents of `%LOCALAPPDATA%\PartLock\error.log`.
