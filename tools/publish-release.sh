#!/usr/bin/env bash
set -euo pipefail

if gh release view "$TAG" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
  echo "Preserving existing release $TAG"
  exit 0
fi

ZIP="$PURR2D2_DIST_DIR/purr2d2-$VERSION.zip"
test -s "$ZIP"
test -s "$ZIP.sha256"
EXTRA=(--repo "$GITHUB_REPOSITORY")
[[ "$PRERELEASE" == true ]] && EXTRA+=(--prerelease --latest=false)
gh release create "$TAG" "$ZIP" "$ZIP.sha256" \
  --target "$GITHUB_SHA" \
  --title "$TITLE" \
  --notes "**Updating:** back up \`settings.json\` and \`Generated/\` from your existing \`purr2d2/\` folder, delete that old mod folder, extract the new \`purr2d2/\` folder, then restore your saved settings and generated content. Restart KSA after updating." \
  --generate-notes \
  "${EXTRA[@]}"
