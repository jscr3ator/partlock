# PartLock — team guide

PartLock is the small app that keeps our SolidWorks files from stepping on each other. It talks to Git and Git LFS for you: you check a part out, edit it, and check it back in. **You never need to use git commands.**

## Install once (5 minutes)

1. Install **Git for Windows** from <https://git-scm.com/download/win>. Click Next through the default options. Do not untick **Git LFS** or **Git Credential Manager** — PartLock needs both.
2. Run **`PartLock.exe`**. There is nothing to install; keep it anywhere handy (Desktop is fine).
3. Paste the repository URL your lead gave you (it looks like `https://github.com/your-org/cad.git`).
4. Choose a folder for your copy (the default under `Documents` is fine) and click **Clone** / **Open**.
5. Your browser (or a small Git Credential Manager window) will ask you to sign in to GitHub. Sign in with your **own** GitHub account and approve. You only do this once per computer.

## The daily loop

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
