#!/usr/bin/env bash
# Build ValheimVillages in Release and assemble a Thunderstore-ready zip in dist/.
#
# Run this locally — compiling requires Valheim's managed assemblies (referenced
# from your Steam/BepInEx install via the .csproj), which are NOT redistributable
# and so are not available on a stock CI runner.
#
# The version is read from Plugin.cs's PluginVersion (the single source of truth,
# since that's what BepInEx reports at runtime) and injected into the packaged
# manifest.json, so you only bump it in one place. Keep the AssemblyVersion /
# AssemblyFileVersion attributes at the top of Plugin.cs in sync by hand.
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"

CSPROJ="src/ValheimVillages/ValheimVillages.csproj"
PLUGIN="src/ValheimVillages/Plugin.cs"
VERSION="$(grep -oP 'PluginVersion\s*=\s*"\K[^"]+' "${PLUGIN}" | head -1)"
if [[ -z "${VERSION}" ]]; then
  echo "error: could not read PluginVersion from ${PLUGIN}" >&2
  exit 1
fi
echo "Packaging ValheimVillages v${VERSION}"

# 1. Build Release (HotReload=false so it produces a clean plugins-style output
#    rather than dropping into the live BepInEx scripts/ hot-reload folder) and
#    capture the DLL path MSBuild reports rather than guessing the OutputPath.
BUILD_LOG="$(dotnet build "${CSPROJ}" -c Release -p:HotReload=false)"
echo "${BUILD_LOG}"
DLL="$(printf '%s\n' "${BUILD_LOG}" | grep -oP 'ValheimVillages -> \K.*ValheimVillages\.dll' | head -1)"
if [[ -z "${DLL}" || ! -f "${DLL}" ]]; then
  echo "error: could not locate built ValheimVillages.dll from the build output" >&2
  exit 1
fi
echo "Using DLL: ${DLL}"

# 2. Assemble the Thunderstore package layout in a staging dir.
STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT
cp Thunderstore/icon.png README.md CHANGELOG.md LICENSE "${STAGE}/"
# Sync the manifest's version_number to PluginVersion as we stage it.
sed -E "s/(\"version_number\"[[:space:]]*:[[:space:]]*\")[^\"]*\"/\1${VERSION}\"/" \
  Thunderstore/manifest.json > "${STAGE}/manifest.json"
mkdir -p "${STAGE}/plugins/ValheimVillages"
cp "${DLL}" "${STAGE}/plugins/ValheimVillages/"

# 3. Zip it.
mkdir -p dist
OUT="${ROOT}/dist/ValheimVillages-${VERSION}.zip"
rm -f "${OUT}"
( cd "${STAGE}" && zip -r -q "${OUT}" . )
echo "Wrote ${OUT}"
