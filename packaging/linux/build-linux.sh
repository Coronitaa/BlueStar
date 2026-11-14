#!/usr/bin/env bash
# Builds BlueStar for Linux: dist/linux/BlueStar-v<ver>-linux-x64.tar.gz and .AppImage.
# Optional API keys (same as build-release.ps1 on Windows): BLUESTAR_DEPOTBOX_API_KEY and BLUESTAR_MODKEYS_JSON
# (the content of modkeys.json). They are encrypted into bluestar.dat with a per-build key that only lives
# inside BlueStar.Infrastructure.dll; nothing is written in plain text.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
version="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)}"
out="$root/dist/linux"
appdir="$out/BlueStar.AppDir"
lib="$appdir/usr/lib/bluestar"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
rm -rf "$out"
mkdir -p "$lib"

secrets_key=""
if [ -n "${BLUESTAR_DEPOTBOX_API_KEY:-}" ] || [ -n "${BLUESTAR_MODKEYS_JSON:-}" ]; then
  openssl rand 64 > "$work/key.bin"
  secrets_key="$(base64 -w0 < "$work/key.bin")"
fi

dotnet publish src/BlueStar.Linux -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -p:Version="$version" -p:InformationalVersion="$version" \
  ${secrets_key:+"-p:BundledSecretsKey=$secrets_key"} -o "$lib"
find "$lib" -name '*.pdb' -delete
chmod +x "$lib/BlueStar"
find "$lib/tools" -name '*.sh' -exec chmod +x {} +

if [ -n "$secrets_key" ]; then
  # Same format as BundledSecrets.cs: base64(IV | AES-256-CBC(json) | HMAC-SHA256(IV | cipher)).
  KEY_FILE="$work/key.bin" OUT_FILE="$lib/bluestar.dat" python3 - <<'PY'
import base64, hmac, hashlib, json, os, subprocess
mod = json.loads(os.environ.get("BLUESTAR_MODKEYS_JSON") or "{}")
data = {k: (mod.get(k) or None) for k in ["curseForge", "nexusMods", "modIo", "thunderstore", "modIoApiUrl"]}
data["defaultApiKey"] = (os.environ.get("BLUESTAR_DEPOTBOX_API_KEY") or "").strip() or None
key = open(os.environ["KEY_FILE"], "rb").read()
iv = os.urandom(16)
cipher = subprocess.run(["openssl", "enc", "-aes-256-cbc", "-K", key[:32].hex(), "-iv", iv.hex()],
                        input=json.dumps(data, separators=(",", ":")).encode(), capture_output=True, check=True).stdout
signed = iv + cipher
mac = hmac.new(key[32:], signed, hashlib.sha256).digest()
open(os.environ["OUT_FILE"], "w").write(base64.b64encode(signed + mac).decode())
plain = [v for v in data.values() if v and len(v) >= 8 and not v.startswith("https://")]
for dirpath, _, files in os.walk(os.path.dirname(os.environ["OUT_FILE"])):
    for f in files:
        if f.endswith((".json", ".txt", ".config", ".xml", ".dat", ".ini")):
            text = open(os.path.join(dirpath, f), "rb").read()
            if any(p.encode() in text for p in plain):
                raise SystemExit(f"API key in plain text in {f}")
print(f"keys: {sum(1 for v in data.values() if v)} encrypted into bluestar.dat")
PY
fi

tar -C "$appdir/usr/lib" -czf "$out/BlueStar-v$version-linux-x64.tar.gz" bluestar

install -m 755 packaging/linux/AppRun "$appdir/AppRun"
install -m 644 packaging/linux/bluestar.desktop "$appdir/bluestar.desktop"
install -m 644 src/BlueStar.App/Assets/bluestar.png "$appdir/bluestar.png"
tool="$work/appimagetool"
curl -fsSL -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/${APPIMAGETOOL_VERSION:-1.9.0}/appimagetool-x86_64.AppImage"
chmod +x "$tool"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$out/BlueStar-v$version-linux-x64.AppImage"

(cd "$out" && sha256sum ./*.tar.gz ./*.AppImage | tee SHA256SUMS)
