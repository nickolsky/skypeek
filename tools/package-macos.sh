#!/bin/sh
# Builds Skypeek for macOS on a Mac (needs the .NET 10 SDK): Skypeek.app, and with Velopack the .pkg installer and the
# signed update feed for the channel osx-<arch>, ready to add to the GitHub release (tools/release-github.ps1 uploads
# everything in dist/upload).
#
#   tools/package-macos.sh                         unsigned, for this Mac's architecture
#   ARCH=x64 tools/package-macos.sh                Intel build
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)" #   INSTALLER_IDENTITY="Developer ID Installer: Name (TEAMID)" #   NOTARY_PROFILE=skypeek tools/package-macos.sh  signed and notarized; create the profile once with
#                                                  xcrun notarytool store-credentials skypeek --apple-id … --team-id …
#   APP_ONLY=1 tools/package-macos.sh              only Skypeek.app and a zip (no installer, feed or signing key)
#
# The update feed is signed with the release key like the other platforms (skypeek-release sign: set
# SKYPEEK_SIGNING_KEY to the key file copied from the release machine, or sign dist/upload/releases.osx-*.json there).
# Without Apple signing, macOS asks for confirmation on first start (right-click → Open, or System Settings → Privacy
# & Security → Open Anyway).
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
[ -n "${APP_ONLY:-}" ] && exit 0

# Installer (.pkg) and update packages. packId "SkypeekApp" and the rid-named channel match tools/publish.ps1.
channel="$rid"
releases="$root/dist/releases/$channel"
upload="$root/dist/upload"
mkdir -p "$releases" "$upload"
dotnet tool restore --tool-manifest "$root/dotnet-tools.json" >/dev/null
set -- pack --packId SkypeekApp --packVersion "$version" --packDir "$app" --mainExe Skypeek     --packTitle "Skypeek for AWS" --packAuthors "Artem Nickolsky" --icon "$root/src/Skypeek.Desktop/Assets/Skypeek.icns"     --channel "$channel" --runtime "$rid" --outputDir "$releases"
[ -n "${SIGN_IDENTITY:-}" ] && set -- "$@" --signAppIdentity "$SIGN_IDENTITY" --signEntitlements "$root/packaging/macos/entitlements.plist"
[ -n "${INSTALLER_IDENTITY:-}" ] && set -- "$@" --signInstallIdentity "$INSTALLER_IDENTITY"
[ -n "${NOTARY_PROFILE:-}" ] && set -- "$@" --notaryProfile "$NOTARY_PROFILE"
dotnet vpk "$@"

dotnet build "$root/tools/Skypeek.ReleaseTool" -c Release -v q -nologo >/dev/null
dotnet "$root/tools/Skypeek.ReleaseTool/bin/Release/net10.0/skypeek-release.dll" sign "$releases/releases.$channel.json"
cp "$releases/releases.$channel.json" "$releases/releases.$channel.json.sig" "$upload/"
cp "$releases"/SkypeekApp-"$version"-*.nupkg "$upload/"
cp "$releases"/*.pkg "$upload/Skypeek-$rid.pkg"
echo "Release files for $channel copied to $upload (add them to the release with tools/release-github.ps1)"
