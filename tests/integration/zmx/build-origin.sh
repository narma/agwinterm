#!/usr/bin/env bash
# Explicit isolated developer setup; never modifies the installed app or toolchain.
set -euo pipefail
umask 077
root=${1:?absolute new build directory required}
source_dir=${2:?uploaded source directory required}
[[ $root == /* && ! -e $root && $(uname -s) == Darwin ]] || exit 2
export PATH="$HOME/.cargo/bin:/opt/homebrew/bin:/usr/local/bin:$PATH"
for tool in git cargo curl python3 shasum; do command -v "$tool" >/dev/null; done
mkdir -m 700 "$root"
exec > >(tee "$root/build.log") 2>&1
trap 'printf "%s\n" "$?" > "$root/build.exit"' EXIT
export GIT_TERMINAL_PROMPT=0 ZIG_GLOBAL_CACHE_DIR="$root/zig-cache"
curl --fail --location --max-time 300 https://ziglang.org/download/0.16.0/zig-aarch64-macos-0.16.0.tar.xz -o "$root/zig.tar.xz"
printf '%s  %s\n' b23d70deaa879b5c2d486ed3316f7eaa53e84acf6fc9cc747de152450d401489 "$root/zig.tar.xz" | shasum -a 256 -c
tar -xJf "$root/zig.tar.xz" -C "$root"
zig="$root/zig-aarch64-macos-0.16.0/zig"
# macOS 27 SDK shim, limited to this private build toolchain (same fix as agterm setup).
float_h="$root/zig-aarch64-macos-0.16.0/lib/include/float.h"
if ! grep -q '__need_infinity_nan' "$float_h"; then
    cp "$float_h" "$float_h.orig"
    { printf '#if defined(__need_infinity_nan)\n#undef INFINITY\n#undef NAN\n#define INFINITY (__builtin_inff())\n#define NAN (__builtin_nanf(""))\n#undef __need_infinity_nan\n#else\n'; cat "$float_h.orig"; printf '\n#endif\n'; } > "$float_h"
fi
checkout() {
    git init -q "$root/$1"
    git -C "$root/$1" remote add origin "$2"
    git -C "$root/$1" fetch -q --depth 1 origin "$3"
    git -C "$root/$1" -c advice.detachedHead=false checkout -q FETCH_HEAD
    [[ $(git -C "$root/$1" rev-parse HEAD) == "$3" ]]
}
agterm_rev=9133258994a6cafb330ee354df949b76b1b2834b
zmx_rev=8bab1f0173b07e79835ea372d749af3dbf0d0842
checkout agterm https://github.com/umputun/agterm "$agterm_rev"
checkout zmx https://github.com/neurosnap/zmx "$zmx_rev"
ghostty_rev=$(sed -n 's|.*ghostty-org/ghostty#\([0-9a-f]\{40\}\)".*|\1|p' "$root/zmx/build.zig.zon")
[[ $ghostty_rev =~ ^[0-9a-f]{40}$ ]]
checkout zmx-ghostty https://github.com/ghostty-org/ghostty "$ghostty_rev"
patches="$root/agterm/scripts/zmx-patches"
for patch in "$patches"/ghostty/*.patch; do git -C "$root/zmx-ghostty" apply --whitespace=nowarn "$patch"; done
for patch in "$patches"/*.patch; do git -C "$root/zmx" apply --whitespace=nowarn "$patch"; done
python3 "$source_dir/tests/integration/zmx/patch-existing-fd.py" "$root/zmx"
python3 "$source_dir/tests/integration/zmx/patch-primary-replay.py" "$root/zmx"
python3 "$source_dir/tests/integration/zmx/patch-runtime-build.py" "$root/zmx"
(cd "$root/zmx"; "$zig" build -Doptimize=ReleaseSafe -Dtarget=aarch64-macos.14.0)
cp -R "$source_dir/native/agwinterm-remote" "$root/bridge"
cp -R "$source_dir/native/agwinterm-zmx" "$root/agwinterm-zmx"
printf '\n[workspace]\n' >> "$root/bridge/Cargo.toml"
(cd "$root/bridge"; cargo build --release)
mkdir "$root/bin"
cp "$root/bridge/target/release/agwinterm-remote" "$root/bin/"
cp "$root/zmx/zig-out/bin/zmx" "$root/bin/zmx-existing-client"
# Manifest publication is intentionally separate: acceptance must prove the FD seam first.
printf 'agterm=%s\nzmx=%s\nghostty=%s\nzig=0.16.0\n' "$agterm_rev" "$zmx_rev" "$ghostty_rev" > "$root/provenance.txt"
shasum -a 256 "$root/bin/"* "$patches/"*.patch "$patches/ghostty/"*.patch >> "$root/provenance.txt"
echo 'Build ready; verify inherited-FD behavior before publishing the client manifest.'
