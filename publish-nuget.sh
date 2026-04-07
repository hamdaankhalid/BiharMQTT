#!/usr/bin/env bash
set -euo pipefail

# ── BiharMQTT local NuGet publish script ──
#
# Usage:
#   ./publish-nuget.sh <NUGET_API_KEY> [VERSION]
#
# Examples:
#   ./publish-nuget.sh nug3t-k3y-here              # auto-version 1.0.0
#   ./publish-nuget.sh nug3t-k3y-here 1.2.3        # explicit version
#
# Prerequisites:
#   - .NET SDK 8.0+ installed
#   - A nuget.org API key (https://www.nuget.org/account/apikeys)

if [ $# -lt 1 ]; then
    echo "Usage: $0 <NUGET_API_KEY> [VERSION]"
    echo ""
    echo "  NUGET_API_KEY  Your nuget.org API key"
    echo "  VERSION        Package version (default: 1.0.0)"
    exit 1
fi

API_KEY="$1"
VERSION="${2:-1.0.0}"
CONFIGURATION="Release"
OUTPUT_DIR="$(pwd)/artifacts"

echo "══════════════════════════════════════════"
echo "  BiharMQTT NuGet Publish"
echo "  Version: $VERSION"
echo "══════════════════════════════════════════"

# Clean
echo ""
echo "→ Cleaning previous artifacts..."
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Build + Pack
echo "→ Building and packing (v$VERSION)..."
dotnet build MQTTnet.sln \
    --configuration "$CONFIGURATION" \
    /p:GeneratePackageOnBuild=true \
    /p:PackageVersion="$VERSION" \
    /p:FileVersion="$VERSION" \
    /p:AssemblyVersion="$VERSION"

# Collect .nupkg files
echo "→ Collecting packages..."
find Source -name "*.nupkg" -exec cp {} "$OUTPUT_DIR/" \;
find Source -name "*.snupkg" -exec cp {} "$OUTPUT_DIR/" \;

PACKAGES=("$OUTPUT_DIR"/*.nupkg)
if [ ${#PACKAGES[@]} -eq 0 ]; then
    echo "✗ No .nupkg files found. Build may have failed."
    exit 1
fi

echo "  Found ${#PACKAGES[@]} package(s):"
for pkg in "${PACKAGES[@]}"; do
    echo "    $(basename "$pkg")"
done

# Push
echo ""
echo "→ Pushing to nuget.org..."
for pkg in "${PACKAGES[@]}"; do
    dotnet nuget push "$pkg" \
        --api-key "$API_KEY" \
        --source https://api.nuget.org/v3/index.json \
        --skip-duplicate
done

echo ""
echo "✓ Done! Packages published to nuget.org."
