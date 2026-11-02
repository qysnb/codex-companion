"""Build the small native Windows launcher with MinGW-w64 (no downloads)."""
from pathlib import Path
import argparse
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def build(output, compiler="gcc", resource_compiler="windres"):
    output = Path(output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    cc = shutil.which(compiler)
    rc = shutil.which(resource_compiler)
    if not cc or not rc:
        raise RuntimeError("MinGW-w64 gcc and windres are required to build the launcher.")
    resource = output.parent / "launcher-resource.o"
    subprocess.run([rc, "-I", str(ROOT / "assets"), "-I", str(ROOT / "src/Companion.Launcher"),
                    "-i", str(ROOT / "src/Companion.Launcher/launcher.rc"), "-o", str(resource)], check=True, cwd=ROOT)
    subprocess.run([cc, "-std=c11", "-Os", "-Wall", "-Wextra", "-Werror", "-mwindows", "-static-libgcc", "-s",
                    str(ROOT / "src/Companion.Launcher/launcher.c"), str(resource), "-o", str(output)], check=True, cwd=ROOT)
    return output

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    result = build(args.output)
    print(f"Built {result.name}: {result.stat().st_size} bytes")
