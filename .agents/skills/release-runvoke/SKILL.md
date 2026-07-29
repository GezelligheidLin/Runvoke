---
name: release-runvoke
description: Validate, version, commit, tag, push, and publish Runvoke releases through the repository GitHub Actions workflow. Use when asked to release Runvoke, bump its version, prepare release notes, create a release tag, publish installers, or verify a Runvoke GitHub Release.
---

# Release Runvoke

Publish Runvoke through the existing tag-triggered GitHub Actions workflow. Preserve unrelated work and never force-push or replace an existing tag.

## 1. Inspect before changing

1. Read `AGENTS.md`, `.github/workflows/release.yml`, `RELEASE_NOTES.md`, `package.json`, and `src-tauri/tauri.conf.json`.
2. Run `git status --short`, inspect staged and unstaged diffs, confirm the current branch and `origin` URL, and list recent tags.
3. Confirm that no credentials, signing keys, generated installers, or unrelated user files will enter the commit.
4. Derive the version from the requested release level. If no level is specified, increment the patch version and ensure the tag does not already exist locally or remotely.

## 2. Prepare the release

1. Set the same version in `package.json` and `src-tauri/tauri.conf.json`. Do not change `src-tauri/Cargo.toml` unless the repository release workflow begins validating it.
2. Replace `RELEASE_NOTES.md` with user-facing notes for the new version. Use only applicable sections:
   - `新增功能`
   - `问题修复`
   - `体验优化`
3. Describe observable changes and important reliability or performance work. Do not include unimplemented claims or test output.
4. Keep project documentation synchronized when the release changes product scope or acceptance criteria.

## 3. Validate

Run all checks before committing:

```powershell
pnpm install --frozen-lockfile
pnpm typecheck
pnpm build
cargo fmt --manifest-path src-tauri/Cargo.toml -- --check
cargo test --manifest-path src-tauri/Cargo.toml
git diff --check
```

Also verify:

- `package.json.version` equals `src-tauri/tauri.conf.json.version`.
- The intended tag is exactly `v<version>`.
- The release workflow still builds signed updater artifacts and publishes `latest.json`.
- `RELEASE_NOTES.md` contains the content intended for the GitHub Release body.

Stop before publishing if a required check fails, the signing workflow is missing required configuration, or the target tag already exists.

## 4. Commit and publish

1. Stage only the intended release files and implementation changes.
2. Review `git diff --cached --stat` and key staged diffs.
3. Commit using the repository convention:

```text
release(other): 发布 v<version>

- 3–5 条高信号中文变更摘要
```

4. Push the release commit to the current upstream branch.
5. Create an annotated tag `v<version>` with a short release summary, then push that exact tag.
6. Do not create a second manual GitHub Release when the tag workflow is responsible for creating it.

## 5. Verify publication

1. Confirm the remote branch and tag point to the release commit.
2. Monitor the tag-triggered GitHub Actions run until completion.
3. Confirm the GitHub Release body matches `RELEASE_NOTES.md` and includes the NSIS installer, its signature, and `latest.json`.
4. Report the version, commit, tag, validation results, workflow or Release URL, and any checks that could not be completed.
