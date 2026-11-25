## What's new in v0.8.0
- **New "Other files" tab – PartLock replaces GitHub Desktop.** Everything that isn't CAD (code, .txt, MATLAB, spreadsheets, docs…) lives here, with no check-out needed:
  - **My changes:** PartLock spots edited, new and deleted files by itself within a couple of seconds. They're ticked automatically; click one to see exactly what changed (green = added, red = removed). Type a message and click **Upload**. **Discard** throws away changes you don't want.
  - **Incoming:** what teammates uploaded that you don't have yet, with who, when and their message. **Get Latest** brings it down.
  - **History:** recent check-ins and uploads for the whole vault, each with its files and line-by-line changes.
  - The tab title shows how many files changed, and the blue bar offers **Upload other files** so nothing gets forgotten.
- **CAD tab** is the familiar list, now only CAD files. Check Out / Check In stay there.
- **Two people edited the same lines?** Like GitHub Desktop, PartLock takes your teammate's version and puts both versions in the file between `<<<<<<<` and `>>>>>>>` lines ("teammate's version" / "your version"). The file shows **Fix overlap**; keep what you want, delete the marker lines, save and upload. Nothing is lost and PartLock won't upload a file that still has markers in it.
- Mac: smaller download.

## 🍎 Now on Mac
**PartLock for macOS** is here: download **PartLock.dmg** below (one download for Apple Silicon and Intel Macs).

1. Open **PartLock.dmg** and drag **PartLock** onto **Applications**.
2. Open PartLock from Applications. The first time, macOS says it can't check it for malware (PartLock isn't registered with Apple). Follow **README - How to open PartLock** in the DMG window ([Apple's guide](https://support.apple.com/en-us/102445)): *System Settings → Privacy & Security → Open Anyway*. Only needed once.
3. If macOS asks to install the **Command Line Tools**, click Install. That's Apple's Git, which PartLock needs.
4. Sign in to GitHub in the browser window that opens, pick your team's repository, done.

Same features as Windows: check out/in, locks, Get Latest, history, the changes bar, file-name search, GitHub account + repository switcher, team chat notifications with your GitHub picture, and in-app updates. Sub-assembly and part lists come from the lists Windows teammates save when they check in assemblies (Macs can't run SolidWorks). Edited code and other files work the same way.

Windows users: nothing changes for you in this update.

## v0.7.1
- **Team notifications post as the person.** In Discord, each message now shows the person's **GitHub profile picture and GitHub name** (synced from the account they're signed in with), and just says what happened, e.g. "🔒 Just checked out `Wing_Assembly.SLDASM`, `WING_SKIN.SLDPRT` in **DBF-Plane-V1**". Check-ins include their message underneath. Slack and Teams don't allow changing the sender, so there the GitHub name is in the text instead.

## v0.7.0
- **Team chat notifications.** PartLock can post to a Discord, Slack or Microsoft Teams channel whenever someone checks out, checks in (with their message), undoes a check-out, or a lead force-unlocks a file. For example: "✅ **Samuel Joseph (@sjcreator06)** checked in `Wing_Assembly.SLDASM` and 3 more: *thicker spar caps*".
  - **Set up once (team lead):** create a webhook in your chat app (Discord: Edit Channel → Integrations → Webhooks → Copy Webhook URL), then in PartLock click 📁 → **Team notifications…**, paste it, **Send test message**, **Save for the team**.
  - It's saved in the vault (`.partlock/config`), so everyone's PartLock posts to it after their next Get Latest. Pick which events post with the checkboxes; clear the link to turn it off.
  - Posting never blocks or slows a check-in; if the chat service is down the message is just skipped.

## v0.6.0
- **Checking out an assembly now includes its parts too, not just sub-assemblies.** PartLock lists every sub-assembly and part the assembly uses (all levels), all ticked and labelled *Sub-assembly* / *Part*. Click **Check out all ticked** (or untick any you won't edit). Anything someone else has checked out is listed and skipped. Toolbox/library files outside the vault and virtual/imported parts stored inside the assembly aren't included.
- Works without SolidWorks open, same as v0.5 (open SolidWorks → saved list → hidden SolidWorks). Lists saved by v0.5 only had sub-assemblies, so those are looked up again once.
- Checking out many files (e.g. a whole aircraft, ~40 files) can take a minute.

## v0.5.1
- **Sub-assemblies without opening SolidWorks.** Checking out an assembly now finds its sub-assemblies even when SolidWorks is closed:
  1. If SolidWorks is open, it's asked directly (instant).
  2. Otherwise PartLock uses the **saved list** for that exact version of the assembly. The list is saved in the vault (`.partlock/refs/`) whenever someone checks in an assembly while SolidWorks is open, so teammates get it for free.
  3. Otherwise PartLock **starts SolidWorks hidden in the background**, asks it, and closes it again (can take a minute the first time; nothing opens on screen).
- **Right-click menu stays normal.** PartLock no longer changes your Windows 11 right-click menu. PartLock's Check Out / Check In are under **Show more options** (or **Shift+right-click**). If you switched to the full menu in v0.5.0, this version puts the normal Windows 11 menu back once.

## v0.4.0
- **Checking out an assembly offers its sub-assemblies too.** PartLock asks SolidWorks which sub-assemblies the assembly uses (every level) and shows them ticked: **Check out ticked too**, **Just the assembly**, or **Cancel**. Sub-assemblies someone else has checked out are listed and skipped. Parts aren't included. SolidWorks needs to be open for this; if it isn't, only the assembly is checked out, same as before.
- **In-app updates.** When a new version is out, the bottom-right of PartLock says **"PartLock vX is available – click to update"**. Click it and PartLock downloads the new version, swaps itself and restarts. Your files and check-outs aren't touched. You can also check any time: **@you → Check for PartLock updates…**
- **Right-click in File Explorer.** Right-click any CAD file in the vault → **PartLock** → **Check Out**, **Check In…**, **Undo Check Out** or **Open in PartLock**. On Windows 11 it's under **Show more options** (or Shift+right-click). Turn it off or on from the 📁 repository button → **Right-click menu in File Explorer**.

> Updating from v0.3.0 or older: download PartLock.exe below once and replace your old one. From v0.4.0 on, PartLock updates itself.

### Earlier
- v0.3.0: GitHub account and repository switcher, Check In without Check Out, changes bar, file-name search, team logo.
- v0.2.0: installs Git for you, requires GitHub sign-in.

## Download
- **PartLock-v0.8.0.zip**: unzip anywhere and run `PartLock.exe`, or
- **PartLock.exe**: the app on its own.

## Requirements
- Windows 10/11 (.NET Framework 4.8 is built in)
- A GitHub account with access to your team's repository
- SolidWorks installed, for the sub-assembly check-out (it doesn't need to be open)

Please report problems with the contents of `%LOCALAPPDATA%\PartLock\error.log`.
