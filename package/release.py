import hashlib
import json
import shutil
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

root = Path(__file__).resolve().parent.parent
package = root / "package"
output = root / ".artifacts/release"
output.mkdir(parents=True, exist_ok=True)
files = {
    "StokHouseSetup.exe": package / "setup/StokHouseSetup.exe",
    "StokHouse-mod.zip": package / "StokHouse-mod.zip",
    "modpack.json": package / "modpack.json",
    "LICENSE": root / "LICENSE",
    "THIRD_PARTY_NOTICES.md": root / "THIRD_PARTY_NOTICES.md",
    "RELEASE_NOTES.md": root / "docs/RELEASE_NOTES.md",
}
for name, source in files.items():
    shutil.copyfile(source, output / name)

assets = root / "StokHouseSetup_Source/Setup/obj/project.assets.json"
restore = json.loads(assets.read_text(encoding="utf-8"))
def runtime_version(package):
    for framework in restore["project"]["frameworks"].values():
        for dependency in framework.get("downloadDependencies", []):
            if dependency["name"].lower() != package:
                continue
            minimum, maximum = dependency["version"].strip("[]").split(",")
            if minimum.strip() == maximum.strip():
                return minimum.strip()
    raise ValueError(f"Cannot determine the restored runtime version for {package}")

runtime_packages = {
    "microsoft.netcore.app.runtime.win-x64": ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"),
    "microsoft.windowsdesktop.app.runtime.win-x64": ("LICENSE",),
}
docs_path = output / "StokHouse-docs.zip"
with ZipFile(docs_path, "w", compression=ZIP_DEFLATED) as docs:
    for name in ("README.md", "README.en.md", "LICENSE", "THIRD_PARTY_NOTICES.md",
                 "docs/HOST.md", "docs/RECOVERY.md", "docs/TESTING.md",
                 "docs/RELEASES.md", "docs/RELEASE_NOTES.md", "docs/WORK_BRIEF.md"):
        docs.write(root / name, name)
    for runtime, notices in runtime_packages.items():
        version = runtime_version(runtime)
        for name in notices:
            matches = [Path(folder) / runtime / version / name for folder in restore["packageFolders"]]
            file = next((candidate for candidate in matches if candidate.is_file()), None)
            if file is None:
                raise FileNotFoundError(f"Missing notice for {runtime}: {name}")
            docs.write(file, f"licenses/{runtime}/{name}")

sums = []
for path in sorted([output / name for name in files] + [docs_path]):
    with path.open("rb") as stream:
        sums.append(f"{hashlib.file_digest(stream, 'sha256').hexdigest()}  {path.name}\n")
(output / "SHA256SUMS.txt").write_text("".join(sums), encoding="ascii")
