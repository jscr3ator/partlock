# PartLock — admin setup

This is the team-lead checklist: one GitHub repo, one-time locking setup, then hand the guide to the team.

## 1. Pick the repo

- Use a **GitHub organization** repo, not a personal one. Suggested name: `cad` (the URL becomes `https://github.com/your-org/cad.git`).
- **Private** is recommended; a 15-person student team fits the free plan.
- Seed it with the current CAD files and a short README before the one-time setup, or start empty and add files afterwards.
- Add every team member under **Settings → Collaborators and teams** with **Write** permission. Locks need push access, so Read-only members cannot check anything out.

## 2. Plan the Git LFS budget

CAD files are large; storing and downloading them uses **Git LFS** quota, which is separate from normal Git storage.

| Plan | LFS storage / month | LFS download / month |
|---|---|---|
| GitHub Free (orgs) | 10 GiB | 10 GiB |
| GitHub Team | 250 GiB | 250 GiB ($4/user/month) |

- Extra storage is **$0.07 / GiB / month**; extra download bandwidth is **$0.0875 / GiB**.
- **Add a payment method and a $10 monthly budget alert** under **Settings → Billing and plans → Budgets and alerts**. Without a payment method, LFS is hard-blocked when the free quota runs out and nobody can push or pull — possibly in the middle of a work session. With one, overage just bills a few dollars.
- A `.SLDPRT` is typically 0.5–20 MB. Download bandwidth is the number that surprises people: every teammate pulling a 1 GB assembly costs 1 GB of quota. Tell the team to **Get Latest once per session**, not repeatedly.
- Locking itself (the lock API) does not cost anything.

## 3. One-time locking setup ("Set it up")

One lead does this once, on the machine that has the repo open in PartLock:

1. Get Latest so the folder is clean and up to date.
2. Click **"Set it up (team lead)"** in the PartLock toolbar.
3. Wait for it to finish and confirm the push succeeded.

What it does, in plain terms:

- writes/updates **`.gitattributes`** so the CAD extensions become Git LFS + **lockable**: sldprt, sldasm, slddrw, step, stp, iges, igs, stl, x_t, dxf, dwg, pdf, 3mf (case-insensitive patterns);
- writes **`.gitignore`** entries for SolidWorks temp files (`~$*`) and `*.tmp`;
- runs **`git add --renormalize .`** so existing CAD files are converted to LFS pointers in the *next commit*;
- commits and pushes that change.

**Important:** this does **not** rewrite history. Old versions of the files remain as normal Git objects, so the repo's `.git` size does not shrink. That is fine for a new repo; see the optional slim-down below for an existing one.

Afterwards every teammate should click **Get Latest** (or re-clone if their copy is in a strange state) so they pick up the new lockable attributes. Until they do, PartLock will show everything as not lockable.

## 4. Folder layout

Ask everyone to keep files in these top-level folders so history stays readable:

```
wing/            fuselage/        tail/
landing-gear/    payload/         electronics/
drawings/        (root: README.md, .gitattributes, .gitignore)
```

Don't enforce it with tooling; just put it in the team guide. One subsystem per folder also makes "check in all affected files" obvious.

## 5. Working rules

- **Everyone works on `main`.** No feature branches for CAD. Locks are per-branch on the server, so branches would let two people "lock" the same part invisibly.
- Only **Admin/Maintain** org members can force-unlock someone else's lock. Normal Write members can check out and check in freely, but cannot break another user's lock — that protects the part's owner.
- The repo owner should keep the **member list** current: remove people who leave the team, because a stale lock from a departed member blocks the part until a lead force-unlocks it.
- Ask the team to **check in at least once per session**. Overnight locks are the #1 cause of force-unlocks.

## 6. Optional: slim the history down (destructive)

If the repo already contains many old CAD revisions and `.git` is huge, you can rewrite history so every past version of the CAD files lives in LFS:

```bash
git lfs migrate import --everything \
  --include="*.SLDPRT,*.SLDASM,*.SLDDRW,*.sldprt,*.sldasm,*.slddrw"
git push --force-with-lease --all
```

**Loud warning:** this **rewrites every commit hash**. It must be done before the team starts working, or at a coordinated stop. Afterwards **everyone must delete their local folder and re-clone** — old clones will fail to push and can corrupt each other's work if forced. Take a copy of the repo first. If in doubt, don't do it: the one-time setup above already makes all *future* CAD commits LFS.

## 7. Distribute the app

`PartLock.exe` is a single self-contained Windows executable (no installer, no .NET install needed). Give it to the team either way:

- **GitHub Releases**: tag the repo (e.g. `v1.0.0`), attach `out/PartLock.exe` as a release asset, and pin the release link in the team chat; or
- **Team drive / OneDrive**: drop the exe in a shared folder.

The exe is unsigned, so Windows SmartScreen may warn the first time: **More info → Run anyway**. Mention this in the team guide so nobody panics.

## 8. Build it yourself

Building needs **Docker** (the build runs in the .NET 8 SDK image; no local .NET install and no Windows machine required):

```bash
./build.sh
```

Output: **`out/PartLock.exe`** (.NET Framework 4.8, runs on Windows 10/11 out of the box).

Run the end-to-end tests (spins up a throwaway Gitea server with LFS + locks, where `alice` and `bob` collaborate on a repo):

```bash
./tests/run-e2e.sh
```

## 9. First-day checklist

- [ ] Repo created, all members added with Write access.
- [ ] Payment method + $10 budget alert set up.
- [ ] Lead ran **"Set it up (team lead)"** and the push succeeded.
- [ ] A test part checked out by one person shows **LockedByOther** for another.
- [ ] Git for Windows installed by everyone; each person signed in to GitHub once.
- [ ] Everyone can Get Latest and sees green **Available** rows.
- [ ] Team guide (`docs/TEAM-GUIDE.md`) posted in the team chat.
