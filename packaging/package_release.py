"""Create English-path portable ZIPs with a root launcher and a self-contained plugin."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN = {"auth.json", "settings.json", "sheet.png"}

def verify(package):
    with zipfile.ZipFile(package) as archive:
        if archive.testzip() is not None:
            raise RuntimeError("ZIP CRC verification failed.")
        names = archive.namelist()
        top = names[0].split("/")[0]
        relative = [name[len(top) + 1:] for name in names]
        if any(not name.isascii() or ".." in Path(name).parts for name in names):
            raise RuntimeError("Package paths must be English and cannot escape the package.")
        if {name for name in relative if "/" not in name} != {"CodexCompanion.exe", "README.md"}:
            raise RuntimeError("Only the launcher and quick start belong at the package root.")
        required = {"app/bin/CodexCompanion.exe", "app/bin/CodexCompanion.Cli.exe",
                    "app/.codex-plugin/plugin.json", "app/skills/companion/SKILL.md", ".agents/plugins/marketplace.json"}
        if not required.issubset(relative):
            raise RuntimeError("The runtime, plugin or marketplace entry is missing.")
        if any(Path(name).name.lower() in FORBIDDEN or name.lower().endswith((".ps1", ".pdb", ".key", ".pfx")) for name in names):
            raise RuntimeError("Development or private files entered the package.")
        if len([name for name in relative if name.endswith("/registered.png")]) != 7:
            raise RuntimeError("Seven runtime character strips are required.")
        catalog = json.loads(archive.read(top + "/.agents/plugins/marketplace.json"))
        if catalog["plugins"][0]["source"]["path"] != "./app":
            raise RuntimeError("Marketplace must reference the portable app folder.")
    return True

def package(runtime, launcher, output, lite=False):
    runtime, launcher, output = Path(runtime).resolve(), Path(launcher).resolve(), Path(output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    filename = "codex-companion-lite-win-x64.zip" if lite else "codex-companion-win-x64.zip"
    # This context owns the exact temporary subtree it creates and cleans up.
    with tempfile.TemporaryDirectory(prefix="portable-stage-", dir=output) as temporary:
        stage = Path(temporary) / ("CodexCompanion-Lite" if lite else "CodexCompanion")
        binary = stage / "app/bin"; binary.mkdir(parents=True)
        shutil.copy2(launcher, stage / "CodexCompanion.exe")
        for source in runtime.rglob("*"):
            if not source.is_file(): continue
            relative = source.relative_to(runtime)
            if not str(relative).isascii(): raise RuntimeError("Runtime paths must be English.")
            if source.name.lower() in FORBIDDEN or source.suffix.lower() in (".ps1", ".pfx", ".key"):
                raise RuntimeError("Unexpected private/development runtime file.")
            if source.suffix.lower() == ".pdb" or "references" in relative.parts: continue
            target = binary / relative; target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(source, target)
        for name in (".codex-plugin", "skills"):
            shutil.copytree(ROOT / "plugin" / name, stage / "app" / name)
        catalog = stage / ".agents/plugins/marketplace.json"; catalog.parent.mkdir(parents=True)
        shutil.copy2(ROOT / "packaging/marketplace.json", catalog)
        for name in ("LICENSE", "ASSETS-NOTICE.md"):
            target = stage / "licenses" / name; target.parent.mkdir(exist_ok=True); shutil.copy2(ROOT / name, target)
            shutil.copy2(ROOT / name, stage / "app" / name)
        for source in (ROOT / "docs").iterdir():
            if not source.is_file(): continue
            public = source.name in ("BUILD.md", "INSTALL.md", "SECURITY.md", "VERIFICATION-PUBLIC.md") or re.fullmatch(r"(?:UPDATE-\d+\.\d+\.\d+\.md|VALIDATION-\d+\.\d+\.\d+-\d{4}-\d{2}-\d{2}\.(?:md|json))", source.name)
            if public:
                target = stage / "docs" / source.name; target.parent.mkdir(exist_ok=True); shutil.copy2(source, target)
        shutil.copytree(ROOT / "docs/examples", stage / "docs/examples")
        (stage / "README.md").write_text(
            "# Codex Companion\n\nDouble-click **CodexCompanion.exe** in this folder.\n\n"
            "双击本目录的 CodexCompanion.exe 即可启动。请保留旁边的 app 文件夹，可将整个目录一起移动。\n\n"
            + ("Lite requires .NET 8 Desktop Runtime x64.\n\n" if lite else "This package includes the .NET runtime; no SDK is required.\n\n")
            + "Install/uninstall: [docs/INSTALL.md](docs/INSTALL.md). Source MIT; art terms: [licenses/ASSETS-NOTICE.md](licenses/ASSETS-NOTICE.md).\n", encoding="utf-8")
        target = output / filename
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for source in sorted(stage.rglob("*")):
                if source.is_file(): archive.write(source, source.relative_to(Path(temporary)).as_posix())
    verify(target)
    return {"name": filename, "bytes": target.stat().st_size, "sha256": hashlib.sha256(target.read_bytes()).hexdigest()}

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", required=True); parser.add_argument("--launcher", required=True)
    parser.add_argument("--output", required=True); parser.add_argument("--lite", action="store_true")
    args = parser.parse_args()
    print(json.dumps(package(args.runtime, args.launcher, args.output, args.lite), indent=2))
