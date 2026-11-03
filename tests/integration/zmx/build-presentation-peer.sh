#!/usr/bin/env bash
# Build-only isolated stage-4 peer; never replaces an installed app or stage-2 artifacts.
set -euo pipefail
root=${1:?new /tmp/aw-stage4-build-* root required}
baseline=${2:?existing stage-2 build root required}
revision_patch=${3:-}
[[ $root == /tmp/aw-stage4-build-* && ! -e "$root" && $root != *'/../'* ]]
[[ $baseline == /tmp/aw-stage2-build-* && -x "$baseline/bin/zmx-existing-client" ]]
mkdir -m 700 "$root"
exec > >(tee "$root/build.log") 2>&1
trap 'printf "%s\n" "$?" > "$root/build.exit"' EXIT
pin=c63e5158e637662ded7ba3a12eac4881b24caef1
git init -q "$root/agterm"
git -C "$root/agterm" remote add origin https://github.com/umputun/agterm
git -C "$root/agterm" fetch --depth 1 origin "$pin"
git -C "$root/agterm" checkout -q FETCH_HEAD
[[ $(git -C "$root/agterm" rev-parse HEAD) == "$pin" ]]
if [[ -n $revision_patch ]]; then
    git -C "$root/agterm" apply --check "$revision_patch"
    git -C "$root/agterm" apply "$revision_patch"
    shasum -a 256 "$revision_patch" > "$root/protocol-patch.sha256"
fi
# Reuse only identical libghostty inputs; preserve the verified ZMX regression pair.
diff -r "$baseline/agterm/scripts/ghostty-patches" "$root/agterm/scripts/ghostty-patches"
grep -q 'GHOSTTY_REV="683d8db643b95cf229bfb5fe9fab9ae677920343"' "$root/agterm/scripts/setup.sh"
cp -R "$baseline/agterm/GhosttyKit.xcframework" "$root/agterm/"
for resource in ghostty terminfo zmx; do
    cp -R "$baseline/agterm/agterm/Resources/$resource" "$root/agterm/agterm/Resources/"
done
cp "$baseline/agterm/.zmx-build-stamp" "$root/agterm/"
export PATH="/opt/homebrew/opt/gettext/bin:/opt/homebrew/bin:/usr/local/bin:$PATH"
cd "$root/agterm"
"$baseline/xcodegen/.build/release/xcodegen" generate
xcodebuild -project agterm.xcodeproj -scheme agterm -configuration Release \
    -derivedDataPath "$root/DerivedData" MARKETING_VERSION=0.0.0 \
    CURRENT_PROJECT_VERSION=0.0.0 GIT_COMMIT=c63e515 ARCHS=arm64 build
printf '%s\n' "$pin" > "$root/source-revision.txt"
shasum -a 256 "$root/DerivedData/Build/Products/Release/agterm.app/Contents/MacOS/"* > "$root/build.sha256"
