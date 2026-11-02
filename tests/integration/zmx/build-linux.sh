#!/usr/bin/env bash
# Explicit developer provisioning only. Never installs into PATH or an existing prefix.
set -euo pipefail
export LC_ALL=C
umask 077
if [[ $# != 2 || $1 != --download-zig ]]; then
    echo "usage: bash build-linux.sh --download-zig NEW_ABSOLUTE_LINUX_DIRECTORY" >&2
    exit 2
fi
root=$2
[[ $root == /* && $root != /mnt/* && ! -e $root ]] || { echo 'Require a new Linux filesystem directory' >&2; exit 2; }
for tool in git curl tar sha256sum python3 timeout; do command -v "$tool" >/dev/null; done
mkdir -m 700 -- "$root"
root=$(realpath "$root")
exec > >(tee "$root/build.log") 2>&1
trap 'result=$?; printf "%s\n" "$result" > "$root/build.exit"' EXIT
export ZIG_GLOBAL_CACHE_DIR="$root/zig-cache"
export GIT_TERMINAL_PROMPT=0
unset ZMX_SESSION ZMX_SESSION_PREFIX ZMX_MANAGED ZMX_MANAGED_CLAIM ZMX_DIR
agterm_rev=0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b
zmx_rev=8bab1f0173b07e79835ea372d749af3dbf0d0842
zig_sha=70e49664a74374b48b51e6f3fdfbf437f6395d42509050588bd49abe52ba3d00
[[ $(uname -m) == x86_64 ]] || { echo 'Only x86_64 Linux is pinned'; exit 2; }
curl --fail --location --max-time 300 https://ziglang.org/download/0.16.0/zig-x86_64-linux-0.16.0.tar.xz -o "$root/zig.tar.xz"
printf '%s  %s\n' "$zig_sha" "$root/zig.tar.xz" | sha256sum --check
tar -xJf "$root/zig.tar.xz" -C "$root"
zig="$root/zig-x86_64-linux-0.16.0/zig"
checkout() {
    git init -q "$root/$1"
    git -C "$root/$1" remote add origin "$2"
    timeout 300 git -C "$root/$1" fetch -q --depth 1 origin "$3"
    git -C "$root/$1" -c advice.detachedHead=false checkout -q FETCH_HEAD
    [[ $(git -C "$root/$1" rev-parse HEAD) == "$3" ]]
}
checkout agterm https://github.com/umputun/agterm "$agterm_rev"
checkout zmx https://github.com/neurosnap/zmx "$zmx_rev"
ghostty_rev=$(sed -n 's|.*ghostty-org/ghostty#\([0-9a-f]\{40\}\)".*|\1|p' "$root/zmx/build.zig.zon")
[[ $ghostty_rev =~ ^[0-9a-f]{40}$ ]]
checkout zmx-ghostty https://github.com/ghostty-org/ghostty "$ghostty_rev"
patches="$root/agterm/scripts/zmx-patches"
cat "$patches"/*.patch "$patches"/ghostty/*.patch | sha256sum > "$root/patches.sha256"
(cd "$patches"; sha256sum *.patch ghostty/*.patch) > "$root/patch-files.sha256"
for patch in "$patches"/ghostty/*.patch; do git -C "$root/zmx-ghostty" apply --whitespace=nowarn "$patch"; done
for patch in "$patches"/*.patch; do git -C "$root/zmx" apply --whitespace=nowarn "$patch"; done
python3 "$(dirname "$0")/patch-existing-fd.py" "$root/zmx"
python3 "$(dirname "$0")/patch-primary-replay.py" "$root/zmx"
sha256sum "$(dirname "$0")/patch-existing-fd.py" "$(dirname "$0")/patch-primary-replay.py" > "$root/agwinterm-patch.sha256"
mkdir "$root/licenses"
for project in agterm zmx zmx-ghostty; do cp "$root/$project/LICENSE" "$root/licenses/$project.txt"; done
cp "$root/zig-x86_64-linux-0.16.0/LICENSE" "$root/licenses/zig.txt"
printf 'agterm=%s\nzmx=%s\nghostty=%s\nzig=0.16.0\nzig_archive_sha256=%s\n' "$agterm_rev" "$zmx_rev" "$ghostty_rev" "$zig_sha" > "$root/provenance.txt"
uname -a >> "$root/provenance.txt"
cd "$root/zmx"
timeout 1200 "$zig" build -Doptimize=ReleaseSafe
sha256sum zig-out/bin/zmx >> "$root/provenance.txt"
# This builds only. Runtime PTY probes must use the routed WSL test entry point.
echo "BUILD READY: $root/zmx/zig-out/bin/zmx"
