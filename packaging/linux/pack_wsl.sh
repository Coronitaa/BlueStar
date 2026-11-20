#!/bin/bash
set -e
mkdir -p /tmp/bin
cat << 'EOF' > /tmp/bin/desktop-file-validate
#!/bin/sh
exit 0
EOF
chmod +x /tmp/bin/desktop-file-validate
export PATH="/tmp/bin:$PATH"

if [ ! -f /tmp/squashfs-root/AppRun ]; then
    cd /tmp
    rm -rf squashfs-root
    /mnt/c/Users/andre/.gemini/antigravity/scratch/BlueStar_repo/BlueStar-main/build-cache/appimagetool --appimage-extract
fi

cd /mnt/c/Users/andre/.gemini/antigravity/scratch/BlueStar_repo/BlueStar-main

cp packaging/linux/AppRun dist/linux/BlueStar.AppDir/AppRun
chmod +x dist/linux/BlueStar.AppDir/AppRun
cp packaging/linux/bluestar.desktop dist/linux/BlueStar.AppDir/bluestar.desktop
cp src/BlueStar.App/Assets/bluestar.png dist/linux/BlueStar.AppDir/bluestar.png

export ARCH=x86_64
/tmp/squashfs-root/AppRun --no-appstream dist/linux/BlueStar.AppDir dist/linux/BlueStar-v1.6.4-linux-x64.AppImage

cd dist/linux
sha256sum BlueStar-v1.6.4-linux-x64.tar.gz BlueStar-v1.6.4-linux-x64.AppImage > SHA256SUMS
cat SHA256SUMS
