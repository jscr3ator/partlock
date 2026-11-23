## What's new in v0.3.0
- **See your GitHub account.** The top-right button shows who you're signed in as (with your GitHub picture). Click it to open your profile, change the name/email on your check-ins, or sign out and switch to another GitHub account.
- **Switch repositories easily.** The repository button shows which GitHub repo you're working in (e.g. `your-team/CAD`). Click it to open the repo on GitHub, show the folder, or **switch to another repository**. You pick from a list of every repo your GitHub account can access, and PartLock downloads it if it isn't on this computer yet. First-time setup uses the same list, so there's no need to paste a link.
- **Check In works even if you forgot Check Out.** If you edited and saved a part without checking it out first, Check In now checks it out for you and uploads it (as long as nobody else has it and nobody has uploaded a newer version).
- **You can see what isn't uploaded yet.** PartLock notices saved changes within a couple of seconds. A blue bar says how many files aren't on GitHub yet, with a **Check In now** button. The Check In button turns blue and shows the count.
- **Search finds file names first.** Typing part of a name lists files whose name starts with it, then files whose name contains it. Folders only match when no file name does, or when you type a path like `cad/`.
- Team logo in the app header, window icon and the exe.
- Clearer steps at the top: Get Latest → Check Out → Edit & save → Check In.

### From v0.2.0
- Installs Git for Windows for you if it's missing.
- Requires a GitHub sign-in before it does anything with the server.

## Download
- **PartLock-v0.3.0.zip**: unzip anywhere and run `PartLock.exe`, or
- **PartLock.exe**: the app on its own.

## Requirements
- Windows 10/11 (.NET Framework 4.8 is built in)
- A GitHub account with access to your team's repository (free: https://github.com/signup)

Not yet tested on real Windows with SolidWorks. Please report problems with the contents of `%LOCALAPPDATA%\PartLock\error.log`.
