import hashlib
import json
import shutil
import sys
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
dll_root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else root / "StokHouse_Source/Mod/bin/Release/net6.0"
result = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else root / "package"
result.mkdir(parents=True, exist_ok=True)

def sha(file):
    return hashlib.sha256(file.read_bytes()).hexdigest()

manifest = json.loads((dll_root / "manifest.json").read_text(encoding="utf-8"))
names = ["StokHouse.dll", "StokHouse.Core.dll", "manifest.json"]
names += [f"i18n/{file.name}" for file in sorted((dll_root / "i18n").glob("*.json"))]
assert manifest["EntryDll"] == names[0] and "i18n/default.json" in names
archive = result / "StokHouse-mod.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
    for name in names:
        info = zipfile.ZipInfo(f"StokHouse/{name}", (2026, 11, 21, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        bundle.writestr(info, (dll_root / name).read_bytes())

pack = {
    "gameVersion": "1.6.15",
    "modVersion": manifest["Version"],
    "modSha256": sha(archive),
    "smapiVersion": manifest["MinimumApiVersion"],
    "smapiUrl": "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
    "smapiSha256": "dd01ddca7b566bfe0d3b3d2d03833496abc56c53da976241f2ab443f5484acc4",
    "modFiles": {name: sha(dll_root / name) for name in names},
}
(result / "modpack.json").write_text(json.dumps(pack, indent=2) + "\n", encoding="utf-8")
