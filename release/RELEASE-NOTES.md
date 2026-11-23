## What's new in v0.2.0
- **Git is installed for you.** If Git for Windows (or Git LFS) is missing, PartLock offers to install it: via winget, or by downloading the official installer. Click **Install Git** and approve the Windows permission prompt.
- **GitHub sign-in is required.** On startup PartLock checks this computer is signed in to GitHub. If not, it shows **Sign in with GitHub** (browser sign-in through Git Credential Manager) and won't continue until you're signed in. Expired or revoked sign-ins get cleared and asked for again.
- Your commit name and email are filled in from your GitHub account if they aren't set yet.

## Download
- **PartLock-v0.2.0.zip**: unzip anywhere and run `PartLock.exe`, or
- **PartLock.exe**: the app on its own.

## Requirements
- Windows 10/11 (.NET Framework 4.8 is built in)
- A GitHub account with access to your team's vault (free: https://github.com/signup)

## Getting started
- Team lead: open the repo in PartLock and click **Set it up (team lead)** once. See `docs/ADMIN-SETUP.md`.
- Everyone else: see `docs/TEAM-GUIDE.md`.
- Try the UI with fake data: `PartLock.exe --demo`

Not yet tested on real Windows with SolidWorks. Please report problems with the contents of `%LOCALAPPDATA%\PartLock\error.log`.
