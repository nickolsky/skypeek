#!/bin/sh
# Builds Skypeek.app on a Mac (needs the .NET 10 SDK), optionally signs and notarizes it, and zips it.
#
#   tools/package-macos.sh                         unsigned app for this Mac's architecture
#   ARCH=x64 tools/package-macos.sh                Intel build
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)" tools/package-macos.sh
#                                                  signed with the hardened runtime
#   ... NOTARY_PROFILE=skypeek tools/package-macos.sh
#                                                  also notarized and stapled; create the profile once with
#                                                  xcrun notarytool store-credentials skypeek --apple-id … --team-id …
#
# Without signing, macOS asks for confirmation on first start (right-click → Open, or System Settings → Privacy &
# Security → Open Anyway).
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)
arch=${ARCH:-$( [ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64 )}
rid="osx-$arch"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/Skypeek.Desktop/Skypeek.Desktop.csproj" | head -1)
out="$root/dist/macos-$arch"
app="$out/Skypeek.app"

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# Not single-file: every native library sits in the bundle so it is signed with it.
dotnet publish "$root/src/Skypeek.Desktop" -c Release -r "$rid" -p:SelfContained=true \
    -p:PublishSingleFile=false -p:DebugType=none -o "$app/Contents/MacOS"

sed "s/@VERSION@/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
cp "$root/src/Skypeek.Desktop/Assets/Skypeek.icns" "$app/Contents/Resources/Skypeek.icns"
chmod 755 "$app/Contents/MacOS/Skypeek"

if [ -n "${SIGN_IDENTITY:-}" ]; then
    # Sign the libraries first, then the app with the hardened runtime.
    find "$app/Contents/MacOS" -type f \( -name "*.dylib" -o -name "*.so" \) -exec \
        codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" {} \;
    codesign --force --timestamp --options runtime --entitlements "$root/packaging/macos/entitlements.plist" \
        --sign "$SIGN_IDENTITY" "$app/Contents/MacOS/Skypeek"
    codesign --force --timestamp --options runtime --entitlements "$root/packaging/macos/entitlements.plist" \
        --sign "$SIGN_IDENTITY" "$app"
    codesign --verify --deep --strict "$app"
fi

zip="$out/Skypeek-$version-macos-$arch.zip"
ditto -c -k --keepParent "$app" "$zip"

if [ -n "${SIGN_IDENTITY:-}" ] && [ -n "${NOTARY_PROFILE:-}" ]; then
    xcrun notarytool submit "$zip" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$app"
    rm "$zip"
    ditto -c -k --keepParent "$app" "$zip"
fi

echo "Built $app"
echo "Zip:  $zip"
