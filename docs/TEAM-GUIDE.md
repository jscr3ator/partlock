# PartLock — team guide

PartLock is the small app that keeps our SolidWorks files from stepping on each other. It talks to Git and Git LFS for you: you check a part out, edit it, and check it back in. **You never need to use git commands.**

## Install once (5 minutes)

1. You need a **GitHub account**. No account yet? Make a free one at <https://github.com/signup> and send your username to your lead so they can add you to the vault.
2. Run **`PartLock.exe`**. There is nothing to install; keep it anywhere handy (Desktop is fine).
3. If Git isn't on your computer yet, PartLock offers to **install Git for Windows** for you. Click **Install Git** and say **Yes** when Windows asks for permission. (Prefer to do it yourself? Get it from <https://git-scm.com/download/win> and keep the default options.)
4. PartLock then asks you to **Sign in to GitHub**. Click **Sign in with GitHub**; your browser (or a small Git Credential Manager window) opens. Sign in with your **own** GitHub account and approve. PartLock won't continue until you're signed in, and you only do this once per computer. Your commit name and email are filled in from your GitHub account if they aren't set yet.
5. Pick your team's repository from the list (it shows every GitHub repo your account can open) and click **Download & open**. Not in the list? Ask your lead to add your GitHub account, or use **Paste a link…**.

**Top-right corner:** the 📁 button shows which repository you're in (click it to open it on GitHub or switch to another one), and the **@you** button shows your GitHub account (click it to switch accounts or change the name on your check-ins).

## Handy extras

- **Check out a whole assembly:** check out the main assembly and PartLock lists its sub-assemblies (ticked) so you can check them all out at once. SolidWorks doesn't need to be open (PartLock uses a saved list, or starts SolidWorks hidden for a moment). Parts aren't included; check out the ones you'll edit.
- **Right-click in File Explorer:** right-click a CAD file in the vault → **PartLock** → Check Out / Check In… / Undo Check Out. (Windows 11: it's under **Show more options**, or hold **Shift** while right-clicking.)
- **Updates:** when a new PartLock is out, click the blue "update" link in the bottom-right corner. PartLock updates itself and restarts.

## The daily loop

> Forgot to Check Out before editing? That's OK. Save the file, close it in SolidWorks, and click **Check In**: PartLock checks it out for you and uploads it, as long as nobody else has it. A blue bar at the top tells you when you have changes that aren't on GitHub yet.


1. **Get Latest** — at the start of every session, and before you check anything out.
2. **Check Out** the part(s) you are about to edit. This locks them so nobody else can change them and makes your copy writable.
3. **Edit in SolidWorks.**
4. **Save, then close the file in SolidWorks.** Don't leave it open.
5. **Check In** with a short message, e.g. `Wing spar cap 3 mm thicker`. Everyone else gets it on their next Get Latest.

If you are not sure, Get Latest again before checking in — it is always safe and never throws your own work away.

## What the statuses mean

| Status | Colour | Meaning | What to do |
|---|---|---|---|
| **Available** | green | Up to date, nobody has it locked. The file is read-only on purpose. | Check Out before editing. |
| **OutOfDate** | light blue | Someone checked in a newer version than the one on your PC. | Get Latest. |
| **CheckedOutByMe** | blue | You hold the lock. You can edit it. | Edit, save, close, Check In. |
| **LockedByOther** | orange | A teammate holds the lock. Do not edit your copy. | Wait, or ask them to check in. |
| **ConflictLockedByOther** | red | They hold the lock **and** you changed your copy anyway. Your edits cannot be checked in. | Save your work elsewhere, then Undo Check Out and redo after they finish. |
| **ModifiedNotCheckedOut** | amber | The file changed on your PC but you never checked it out (someone made it writable by hand). | Save a copy, Undo Check Out, then Check Out and redo. |
| **NewFile** | purple | A file that is not in the repository yet. | Check it in. |
| **Changed** | grey | A non-CAD file (notes, docs) has local edits. | Check it in if it should be shared. |
| **Deleted** | grey | A tracked file is missing from your PC. | Check in the deletion if intended, otherwise Get Latest. |

Colours can differ slightly between Windows themes, but the names are what matter.

## Rules

- **Never rename or move parts in Windows Explorer.** SolidWorks assemblies store file paths. Use **File → Rename** inside SolidWorks, or **Pack and Go** when copying a design, then check in **every affected file in the same check-in**.
- **Close files in SolidWorks before checking in.** A check-in while the file is open can capture a half-saved file.
- **Files you haven't checked out open read-only in SolidWorks.** That is intentional protection, not a bug. If you check out a file that was already open, **close it and reopen it** — SolidWorks remembers the old read-only state.
- One part per check-in when you can. Small check-ins are easy to undo.
- Everyone works on the **main** branch. Do not create branches in PartLock or another git tool.
- Don't delete `.git` or `.gitattributes` from your folder.

## If someone forgot to check in

Ask a team lead to **Force Unlock** the file. That releases the lock so you can check the part out. The other person's local copy is not touched, but they can no longer check it in, so they must copy their changes into your new version (or you copy theirs) by hand.

## FAQ

**"SolidWorks says the file is read-only."**
Check the status in PartLock. If it is **Available**, Check Out and reopen the file in SolidWorks. If it is **LockedByOther**, the owner still has it — message them. If you checked it out while it was open, close and reopen it.

**"It says someone checked in at the same time / my check-in failed because the file is behind."**
PartLock automatically pulls, rebases, and retries. If it still fails: click **Get Latest**, close and reopen the file, and redo the last small edit. Do not use a different git tool to force a push.

**"I edited without checking out."**
PartLock shows **ModifiedNotCheckedOut** and will refuse the check-in — that is the protection working. Save your changed file somewhere else, click **Undo Check Out** to restore the official version, then **Check Out** and copy your change back in.

**"I closed PartLock in the middle of a check-in."**
Reopen it and click **Get Latest**. If the file shows **CheckedOutByMe** and modified, check it in again. If it is **Available** and your work is in the file, ask a lead to check the history before doing anything else.

**"Can I work offline?"**
Only for a short while. You can edit files you have checked out, but Check In and Get Latest need the network.
