#!/usr/bin/env bash
# Regenerate decompiled-refs/ — per-type C# sources for the Valheim engine
# assemblies, for reference/search only (git-ignored, never compiled).
# Re-run after a Valheim update. Requires: dotnet tool install -g ilspycmd
#
# Decompiles in place over the existing folder (ilspycmd overwrites each file).
# No deletion: if a type is removed from an assembly its stale .cs may linger,
# which is harmless for a reference tree and rare in practice.
set -euo pipefail

MANAGED="${VALHEIM_MANAGED:-$HOME/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed}"
[ -d "$MANAGED" ] || MANAGED="$HOME/.steam/steam/steamapps/common/Valheim/valheim_Data/Managed"
OUT="$(cd "$(dirname "$0")/.." && pwd)/decompiled-refs"

export PATH="$HOME/.dotnet/tools:$PATH"
export DOTNET_ROLL_FORWARD=Major DOTNET_ROLL_FORWARD_TO_PRERELEASE=1

mkdir -p "$OUT"
for asm in assembly_valheim assembly_utils assembly_guiutils; do
  echo "decompiling $asm ..."
  # -p writes one .cs per type. It also emits a .csproj / Properties/ scaffold;
  # those are git-ignored and outside any built project, so they're left as-is.
  ilspycmd "$MANAGED/$asm.dll" -p -o "$OUT/$asm" >/dev/null
done
echo "done -> $OUT ($(find "$OUT" -name '*.cs' | wc -l) files)"
