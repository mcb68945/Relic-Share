#!/usr/bin/env bash
# Builds the loader and both game-version builds into out/.
# Needs lib/v111/ and lib/v113/, each with sts2.dll, 0Harmony.dll and GodotSharp.dll copied from that game version
# (Slay the Spire 2/data_sts2_windows_x86_64/).
set -e
cd "$(dirname "$0")"
for p in v111/RelicShare.V111.csproj v113/RelicShare.V113.csproj RelicShare.csproj; do dotnet build "$p" -c Release -o out; done
echo "Install: copy out/RelicShare.dll, out/RelicShare.V111.dll, out/RelicShare.V113.dll and RelicShare.json into mods/RelicShare/"
