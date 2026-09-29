#!/bin/sh
# Installs Skypeek for the current user (no root needed):
#   ~/.local/share/skypeek/Skypeek        the self-contained executable
#   ~/.local/bin/skypeek                  a link to it (on PATH in most distributions)
#   ~/.local/share/applications/…         the menu entry
#   ~/.local/share/icons/hicolor/256x256  the icon
# Usage: sh install.sh [path/to/Skypeek]      (default: the Skypeek file next to this script)
#        sh install.sh --uninstall            (the vault in ~/.local/share/Skypeek is kept)
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
app_dir="$data/skypeek"
bin_dir="$HOME/.local/bin"
desktop="$data/applications/skypeek.desktop"
icon="$data/icons/hicolor/256x256/apps/skypeek.png"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$bin_dir/skypeek" "$desktop" "$icon" "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/skypeek.desktop"
    rm -rf "$app_dir"
    echo "Skypeek removed. Your encrypted vault stays in $data/Skypeek (delete it to remove all data)."
    exit 0
fi

source_exe=${1:-"$here/Skypeek"}
if [ ! -f "$source_exe" ]; then
    echo "Skypeek executable not found: $source_exe" >&2
    exit 1
fi

mkdir -p "$app_dir" "$bin_dir" "$(dirname "$desktop")" "$(dirname "$icon")"
cp "$source_exe" "$app_dir/Skypeek"
chmod 755 "$app_dir/Skypeek"
ln -sf "$app_dir/Skypeek" "$bin_dir/skypeek"
[ -f "$here/skypeek.png" ] && cp "$here/skypeek.png" "$icon"

cat > "$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Skypeek
GenericName=AWS status
Comment=AWS status, logs, secrets and network in the tray
Exec="$app_dir/Skypeek"
Icon=skypeek
Terminal=false
Categories=Development;Utility;
StartupWMClass=Skypeek
EOF

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$(dirname "$desktop")" >/dev/null 2>&1 || true
echo "Installed. Start it from the application menu, or run: skypeek"
echo "GNOME needs the AppIndicator extension for the tray icon (preinstalled on Ubuntu); KDE and most others show it as is."
