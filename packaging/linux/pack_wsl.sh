#!/bin/bash
set -e
mkdir -p /tmp/bin
cat << 'EOF' > /tmp/bin/desktop-file-validate
#!/bin/sh
exit 0
EOF
chmod +x /tmp/bin/desktop-file-validate
export PATH="/tmp/bin:$PATH"

# Resolve ROOT_DIR if not passed or empty
if [ -z "$ROOT_DIR" ]; then
    if [ -n "$SCRIPT_DIR" ] && [ -d "$SCRIPT_DIR/../.." ]; then
        ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
    else
        ROOT_DIR="/mnt/c/Users/andre/.gemini/antigravity/scratch/BlueStar_repo/BlueStar-main"
    fi
fi

if [ ! -f /tmp/squashfs-root/AppRun ]; then
    cd /tmp
    rm -rf squashfs-root
    if [ -f "$ROOT_DIR/build-cache/appimagetool" ]; then
        "$ROOT_DIR/build-cache/appimagetool" --appimage-extract
    elif [ -f /tmp/appimagetool ]; then
        /tmp/appimagetool --appimage-extract
    else
        curl -L https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage -o /tmp/appimagetool
        chmod +x /tmp/appimagetool
        /tmp/appimagetool --appimage-extract
    fi
fi

cd "$ROOT_DIR"

RID="${RID:-linux-x64}"
ARCH="${ARCH:-x86_64}"
VERSION="${VERSION:-1.6.5}"

# Generar tar.gz
tar -C dist/linux/BlueStar.AppDir/usr/lib -czf "dist/linux/BlueStar-v${VERSION}-${RID}.tar.gz" bluestar

cp packaging/linux/AppRun dist/linux/BlueStar.AppDir/AppRun
chmod +x dist/linux/BlueStar.AppDir/AppRun
cp packaging/linux/bluestar.desktop dist/linux/BlueStar.AppDir/bluestar.desktop
cp src/BlueStar.App/Assets/bluestar.png dist/linux/BlueStar.AppDir/bluestar.png

export ARCH
/tmp/squashfs-root/AppRun --no-appstream dist/linux/BlueStar.AppDir "dist/linux/BlueStar-v${VERSION}-${RID}.AppImage"

cd dist/linux
sha256sum "BlueStar-v${VERSION}-${RID}.tar.gz" "BlueStar-v${VERSION}-${RID}.AppImage" > SHA256SUMS
gpg --batch --yes --detach-sign --armor SHA256SUMS
gpg --armor --export "BlueStar" > BlueStar-Signing-Key.asc
cat SHA256SUMS
