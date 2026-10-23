#!/usr/bin/env bash
# Builds native/bin/libhushcue_ct2.dylib on macOS, with the CTranslate2 dylib it loads next to it.
#
#     native/build.sh [SITE_PACKAGES]
#
# Needs the Xcode command line tools, CMake and git. The CTranslate2 binary comes from a pip target folder with the
# ctranslate2 4.8.2 wheel (by default native/deps/site-packages):
#
#     python3 -m pip install --no-deps --target native/deps/site-packages ctranslate2==4.8.2
#
# CPU only: CTranslate2's macOS build has no GPU backend.
set -euo pipefail

CT2_VERSION="4.8.2"
root="$(cd "$(dirname "$0")" && pwd)"
site_packages="${1:-$root/deps/site-packages}"
deps="$root/deps"
build="$root/build"
bin="$root/bin"
ct2_dylib_name="libctranslate2.$CT2_VERSION.dylib"

fail() { echo "build.sh: $*" >&2; exit 1; }

[[ "$(uname)" == "Darwin" ]] || fail "macOS only; on Windows use native/build.ps1"
[[ -d "$site_packages/ctranslate2-$CT2_VERSION.dist-info" ]] ||
  fail "expected the ctranslate2 $CT2_VERSION wheel in $site_packages"
ct2_dylib="$site_packages/ctranslate2/.dylibs/$ct2_dylib_name"
[[ -f "$ct2_dylib" ]] || fail "$ct2_dylib_name not found in $site_packages"

# --- CTranslate2 headers, same version as the library
ct2_source="$deps/CTranslate2"
if [[ ! -d "$ct2_source" ]]; then
  git clone --quiet --depth 1 --branch "v$CT2_VERSION" https://github.com/OpenNMT/CTranslate2.git "$ct2_source"
fi

# --- the CTranslate2 library next to the bridge. The wheel's install name is a placeholder from the wheel build
# (/DLC/...), so it gets an @rpath one the bridge can find it by; changing it voids the signature, so re-sign ad hoc.
mkdir -p "$bin"
cp -f "$ct2_dylib" "$bin/$ct2_dylib_name"
install_name_tool -id "@rpath/$ct2_dylib_name" "$bin/$ct2_dylib_name"
codesign --force --sign - "$bin/$ct2_dylib_name"

# --- build the bridge
cmake -S "$root/hushcue_ct2" -B "$build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON -DCMAKE_INSTALL_RPATH="@loader_path" \
  "-DCT2_INCLUDE_DIR=$ct2_source/include" "-DCT2_LIBRARY=$bin/$ct2_dylib_name"
cmake --build "$build" --config Release
cp -f "$build/libhushcue_ct2.dylib" "$bin/"

echo "Built $bin/libhushcue_ct2.dylib with $ct2_dylib_name next to it"
