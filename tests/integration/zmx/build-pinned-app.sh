#!/usr/bin/env bash
# Build-only tooling and artifacts stay under the already authorized isolated build root.
set -euo pipefail
root=${1:?isolated build root required}
[[ $root == /tmp/aw-stage2-build-* && -d "$root/agterm" ]] || exit 2
exec > >(tee "$root/pinned-app.log") 2>&1
trap 'printf "%s\n" "$?" > "$root/pinned-app.exit"' EXIT
export ZIG_GLOBAL_CACHE_DIR="$root/zig-cache"
export PATH="/opt/homebrew/opt/gettext/bin:/opt/homebrew/bin:/usr/local/bin:$PATH"
zig="$root/zig-aarch64-macos-0.16.0/zig"
app="$root/agterm"
[[ $(git -C "$app" rev-parse HEAD) == 9133258994a6cafb330ee354df949b76b1b2834b ]]
xcrun metal --version
if [[ ! -d "$root/app-ghostty/.git" ]]; then
git init -q "$root/app-ghostty"
git -C "$root/app-ghostty" remote add origin https://github.com/ghostty-org/ghostty
git -C "$root/app-ghostty" fetch --depth 1 origin 683d8db643b95cf229bfb5fe9fab9ae677920343
git -C "$root/app-ghostty" checkout -q FETCH_HEAD
for patch in "$app/scripts/ghostty-patches/"*.patch; do git -C "$root/app-ghostty" apply --whitespace=nowarn "$patch"; done
fi
(cd "$root/app-ghostty"; "$zig" build -Doptimize=ReleaseFast -Demit-xcframework=true -Dxcframework-target=native -Demit-macos-app=false)
cp -R "$root/app-ghostty/macos/GhosttyKit.xcframework" "$app/"
mkdir -p "$app/agterm/Resources/ghostty" "$app/agterm/Resources/zmx"
cp -R "$root/app-ghostty/zig-out/share/ghostty/shell-integration" "$root/app-ghostty/zig-out/share/ghostty/themes" "$app/agterm/Resources/ghostty/"
cp -R "$root/app-ghostty/zig-out/share/terminfo" "$app/agterm/Resources/"
cp "$app/agterm/Resources/custom-themes/"* "$app/agterm/Resources/ghostty/themes/"
cp "$root/bin/zmx-existing-client" "$app/agterm/Resources/zmx/zmx"
cp "$root/zmx/LICENSE" "$app/agterm/Resources/zmx/"
printf '8bab1f0173b07e79835ea372d749af3dbf0d0842 aarch64-macos.14.0 aw-stage2-%s\n' "$(shasum -a 256 "$root/bin/zmx-existing-client" | cut -d' ' -f1)" > "$app/.zmx-build-stamp"
if [[ ! -d "$root/xcodegen" ]]; then git clone --depth 1 --branch 2.44.1 https://github.com/yonaskolb/XcodeGen "$root/xcodegen"; fi
(cd "$root/xcodegen"; swift build -c release --product xcodegen)
(cd "$app"; "$root/xcodegen/.build/release/xcodegen" generate
 xcodebuild -project agterm.xcodeproj -scheme agterm -configuration Release -derivedDataPath build/DerivedData MARKETING_VERSION=0.0.0 CURRENT_PROJECT_VERSION=0.0.0 GIT_COMMIT=9133258 ARCHS=arm64 build)
shasum -a 256 "$app/build/DerivedData/Build/Products/Release/agterm.app/Contents/MacOS/"* > "$root/pinned-app.sha256"
