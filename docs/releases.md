# Release maintenance

The release workflow follows the gatOS, unscience and purrTTY rolling release
pattern. Purr2d2 publishes betas from **every** non-main branch. Pushes and manual
branch dispatches publish; pull requests and tag refs do not. PRs do not consume
release tags, though every workflow run consumes a build number. Gaps are normal.

`release-major.txt` is the single manually maintained release major. For example,
major `1` and workflow run `123` produce `1.123.0` on main or `1.123.0-beta`
elsewhere, with tag `v1.123.0` or `v1.123.0-beta` and matching project, manifest,
ZIP and checksum names. Changing the major does not reset the build counter.
The local development version in `Purr2D2.csproj` and `mod.toml` stays in sync
independently; CI stamps both files without committing them.

The .NET Release build owns staging, ZIP creation and hashing. CI validates
source/stage, checks the production runtime bootstrap, then compares every ZIP
file byte-for-byte with stage and verifies its checksum before transferring the
assets to the publishing job. Licensed game DLLs remain compile inputs only.
This does not replace the native game acceptance recorded in `ACCEPTANCE.md`.

The repository requires `KSA_GAME_ASSEMBLIES_PAT`, a fine-grained token with
read-only Contents access to `meow-sci/ksa-game-assemblies`. The workflow pins
`4b3e9e2281c6eec25c9ab77ddf2961ec53ec3dc8` (KSA 2026.10.25.5707).
Full builds run for branch pushes, manual dispatches and same-repository PRs;
fork PRs run the offline policy checks only. No write token is used to build.

Reruns preserve an existing release and its assets. After successful publishing,
cleanup fetches every release page and keeps the newest 10 stable releases or
5 betas by publication time (ID breaks ties). Beta branches share one pool;
cleanup is serialized per channel. All positive major versions participate.
Only published, non-draft `vMAJOR.BUILD.0` / `vMAJOR.BUILD.0-beta` tags with the
matching prerelease flag qualify. Older qualifying releases **and tags** are
deleted; named or other-version releases are left alone.

Local verification (no GitHub writes):

```sh
python3 tools/check-release-policy.py
dotnet build Purr2D2.slnx -c Release -p:RestoreLockedMode=true --nologo -v quiet
python3 tools/validate.py --stage artifacts/stage/purr2d2
python3 tools/check-release-archive.py artifacts/stage/purr2d2 artifacts/dist/purr2d2-0.2.0.zip
python3 tools/check_runtime_bootstrap.py --entry-assembly bin/Release/net10.0/Purr2D2.dll
```

When updating an installed mod, back up `settings.json` and `Generated/`, remove
the old `purr2d2/` folder, extract the new folder, restore that saved content,
and restart KSA. This removes obsolete shipped files while retaining preferences
and generated slice data.
