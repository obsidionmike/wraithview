#!/usr/bin/env bash
# Cross-build WraithView.exe on Linux. Needs Node 18+ and a Microsoft .NET 8+ SDK that includes the
# Windows Desktop targets (Ubuntu's source-built dotnet-sdk-8.0 does not; Microsoft's or conda-forge's does).
#   DOTNET=/path/to/dotnet scripts/build-linux.sh [out-dir]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/release}"
dotnet="${DOTNET:-dotnet}"

echo "Building web UI..."
(cd "$root/app" && npm ci --no-audit --no-fund && npm run build)

echo "Bundling overlay UI..."
rm -f "$root/native/Assets/overlay-dist.zip"
(cd "$root/app/dist" && python3 -c "
import os, zipfile
with zipfile.ZipFile('../../native/Assets/overlay-dist.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    for r, _, fs in os.walk('.'):
        for f in fs:
            p = os.path.join(r, f)
            z.write(p, os.path.relpath(p, '.'))
")

echo "Publishing single-file win-x64 executable..."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$dotnet" publish "$root/native/WraithView.csproj" -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -p:PublishReadyToRun=false -o "$out"
rm -f "$out"/*.xml
sha256sum "$out/WraithView.exe" | tee "$out/WraithView.exe.sha256"
