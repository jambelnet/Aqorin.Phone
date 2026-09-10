#!/usr/bin/env bash
# Publishes Aqorin.Phone for one or all supported runtime identifiers.
#   ./publish.sh            # all RIDs
#   ./publish.sh linux-x64  # one RID
#   SELF_CONTAINED=false ./publish.sh osx-arm64   # framework-dependent
set -euo pipefail
cd "$(dirname "$0")"

rids=("win-x64" "osx-x64" "osx-arm64" "linux-x64")
if [[ $# -gt 0 ]]; then rids=("$@"); fi
self_contained="${SELF_CONTAINED:-true}"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
if [[ -z "$version" ]]; then echo "Could not read <Version> from Directory.Build.props" >&2; exit 1; fi
publish_root="publish/$version"

for rid in "${rids[@]}"; do
  out="$publish_root/$rid"
  echo "== Publishing $rid -> $out"
  dotnet publish src/Aqorin.Phone.App/Aqorin.Phone.App.csproj \
    --configuration Release --runtime "$rid" --self-contained "$self_contained" --output "$out"
  case "$rid" in
    win-*)   native=portaudio.dll ;;
    osx-*)   native=libportaudio.dylib ;;
    linux-*) native=libportaudio.so ;;
  esac
  if [[ ! -f "$out/$native" ]]; then echo "native PortAudio library $native missing from $out" >&2; exit 1; fi
  echo "   OK: $native present"
done

iss_path="installer/Aqorin.Phone.iss"
win_source="$publish_root/win-x64"
if [[ -f "$iss_path" ]]; then
  echo "== Windows installer script: $iss_path"
  echo "   Build it with: iscc \"$iss_path\" /DMyAppVersion=$version /DSourceDir=\"$win_source\""
fi
