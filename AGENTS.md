# README.md management

Do not update the README.md when feature and bugfix work is done in this repo.

README.md is user-facing documentation for this mod and will be maintained manually.

# GitHub Actions management

The github actions only build releases if files which impact the built mod change.

When the project is modified in a way where those inputs which should trigger a mod release change, the 
`repos/purr2d2/.github/workflows/release.yml` action definitions must be updated to match

