import os
import sys
import zlib
import winreg
import configparser
import re

def get_steam_path():
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam")
        val, _ = winreg.QueryValueEx(key, "SteamPath")
        winreg.CloseKey(key)
        return val
    except Exception:
        return r"d:\Steam"

def make_vdf_string(key, value):
    return b'\x01' + key.encode('utf-8') + b'\x00' + value.encode('utf-8') + b'\x00'

def make_vdf_int(key, value):
    return b'\x02' + key.encode('utf-8') + b'\x00' + (value & 0xFFFFFFFF).to_bytes(4, byteorder='little', signed=False)

def build_entry(index, app_name, exe_path, start_dir):
    crc = zlib.crc32((exe_path + app_name).encode('utf-8'))
    appid = crc | 0x80000000
    b = bytearray()
    b.extend(b'\x00' + str(index).encode('utf-8') + b'\x00')
    b.extend(make_vdf_int("appid", appid))
    b.extend(make_vdf_string("AppName", app_name))
    b.extend(make_vdf_string("Exe", f'"{exe_path}"'))
    b.extend(make_vdf_string("StartDir", f'"{start_dir}"'))
    b.extend(make_vdf_string("icon", ""))
    b.extend(make_vdf_string("ShortcutPath", ""))
    b.extend(make_vdf_string("LaunchOptions", ""))
    b.extend(make_vdf_int("IsHidden", 0))
    b.extend(make_vdf_int("AllowDesktopConfig", 1))
    b.extend(make_vdf_int("AllowOverlay", 1))
    b.extend(make_vdf_int("OpenVR", 0))
    b.extend(make_vdf_int("Devkit", 0))
    b.extend(make_vdf_string("DevkitGameID", ""))
    b.extend(make_vdf_int("DevkitOverrideAppID", 0))
    b.extend(make_vdf_int("LastPlayTime", 0))
    b.extend(make_vdf_string("FlatpakAppID", ""))
    b.extend(make_vdf_string("tags", ""))
    b.extend(b'\x08')
    return bytes(b)

curr_dir = os.path.abspath(os.getcwd())
if len(sys.argv) > 3 and sys.argv[3] and os.path.exists(sys.argv[3]):
    curr_dir = os.path.abspath(sys.argv[3])

ini_path = os.path.join(curr_dir, "ReFix.ini")

app_name = ""
if os.path.exists(ini_path):
    config = configparser.ConfigParser(strict=False)
    try:
        config.read(ini_path, encoding='utf-8')
        if 'Game' in config and 'GameName' in config['Game']:
            app_name = config['Game']['GameName'].strip()
    except Exception:
        pass

if not app_name:
    app_name = os.path.basename(curr_dir)

if not app_name.endswith("[ʀᴇꜰɪx]"):
    display_name = f"{app_name} [ʀᴇꜰɪx]"
else:
    display_name = app_name

if len(sys.argv) > 1 and sys.argv[1]:
    display_name = sys.argv[1]
    if not display_name.endswith("[ʀᴇꜰɪx]"):
        display_name = f"{display_name} [ʀᴇꜰɪx]"

target_exe = ""
if len(sys.argv) > 2 and sys.argv[2] and os.path.exists(sys.argv[2]):
    target_exe = os.path.abspath(sys.argv[2])

EXCLUDE_RE = re.compile(r"^(powershell|pwsh|cmd|python|pythonw|git|dotnet|createdump|unitycrash|crashreport|install|unins|setup|updater|select_folder|depot|steam-manifest|vcredist|dxsetup|prereq|ue4prereq)", re.IGNORECASE)

def is_valid_exe(filename):
    name = os.path.splitext(filename)[0]
    return filename.lower().endswith(".exe") and not EXCLUDE_RE.match(name)

if not target_exe:
    # 1. Top-level files in curr_dir
    top_candidates = []
    try:
        for f in os.listdir(curr_dir):
            fp = os.path.join(curr_dir, f)
            if os.path.isfile(fp) and is_valid_exe(f):
                top_candidates.append((fp, os.path.getsize(fp)))
    except Exception:
        pass

    if top_candidates:
        top_candidates.sort(key=lambda x: x[1], reverse=True)
        game_matched = [c for c in top_candidates if app_name.lower() in os.path.basename(c[0]).lower()]
        if game_matched:
            target_exe = game_matched[0][0]
        else:
            target_exe = top_candidates[0][0]

if not target_exe:
    # 2. Check parent directory if inside a subfolder
    parent_dir = os.path.dirname(curr_dir)
    parent_candidates = []
    try:
        for f in os.listdir(parent_dir):
            fp = os.path.join(parent_dir, f)
            if os.path.isfile(fp) and is_valid_exe(f):
                parent_candidates.append((fp, os.path.getsize(fp)))
    except Exception:
        pass
    if parent_candidates:
        parent_candidates.sort(key=lambda x: x[1], reverse=True)
        target_exe = parent_candidates[0][0]

if not target_exe:
    # 3. Recursive search prioritizing Shipping binaries then file size
    rec_candidates = []
    for root, dirs, files in os.walk(curr_dir):
        for f in files:
            if is_valid_exe(f):
                fp = os.path.join(root, f)
                try:
                    rec_candidates.append((fp, os.path.getsize(fp), "shipping" in f.lower()))
                except Exception:
                    pass

    if rec_candidates:
        rec_candidates.sort(key=lambda x: (x[2], x[1]), reverse=True)
        target_exe = rec_candidates[0][0]

if not target_exe:
    print("[ERROR] Could not find executable in directory.")
    sys.exit(1)

exe_dir = os.path.dirname(target_exe) + "\\"

print(f"[+] Target Game Name: {display_name}")
print(f"[+] Executable Path: {target_exe}")
print(f"[+] Working Directory: {exe_dir}")

steam_dir = get_steam_path()
user_data = os.path.join(steam_dir, "userdata")

if not os.path.exists(user_data):
    print(f"[ERROR] Steam userdata directory not found at {user_data}")
    sys.exit(1)

users = [d for d in os.listdir(user_data) if os.path.isdir(os.path.join(user_data, d)) and d.isdigit()]

for u in users:
    vdf_path = os.path.join(user_data, u, "config", "shortcuts.vdf")
    if not os.path.exists(vdf_path):
        os.makedirs(os.path.dirname(vdf_path), exist_ok=True)
        content = b'\x00shortcuts\x00\x08\x08'
    else:
        with open(vdf_path, 'rb') as f:
            content = f.read()

    display_bytes = display_name.encode('utf-8')
    exe_bytes = target_exe.encode('utf-8')

    already_added = False
    if display_bytes in content and exe_bytes in content:
        already_added = True

    if not already_added:
        idx = content.count(b'\x01AppName\x00')
        new_entry = build_entry(idx, display_name, target_exe, exe_dir)
        if content.endswith(b'\x08\x08'):
            new_content = content[:-2] + new_entry + b'\x08\x08'
        elif content.endswith(b'\x08'):
            new_content = content[:-1] + new_entry + b'\x08\x08'
        else:
            new_content = b'\x00shortcuts\x00' + new_entry + b'\x08\x08'
    else:
        new_content = content

    with open(vdf_path, 'wb') as f:
        f.write(new_content)

print(f"[SUCCESS] Shortcut for '{display_name}' updated across all Steam user profiles.")
