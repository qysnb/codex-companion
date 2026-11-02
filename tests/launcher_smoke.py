"""Exercise only our native launcher and an inert local fixture; no real app/network."""
from pathlib import Path
import argparse
import shutil
import subprocess
import tempfile
import time

FIXTURE = r'''
#include <windows.h>
#include <stdio.h>
int WINAPI WinMain(HINSTANCE a,HINSTANCE b,LPSTR c,int d) {
 wchar_t directory[32768]; FILE *proof;
 (void)a;(void)b;(void)c;(void)d;
 if(!GetCurrentDirectoryW(32768,directory)) return 1;
 proof=_wfopen(L"launch-proof.txt",L"wt"); if(!proof) return 2;
 fwprintf(proof,L"%ls",directory);fclose(proof);return 0;
}
'''

def run(launcher):
    # TemporaryDirectory owns this exact, resolved subtree and its cleanup.
    with tempfile.TemporaryDirectory(prefix="companion-launcher-") as temporary:
        base = Path(temporary).resolve()
        package = base / "package with spaces"
        package.mkdir()
        starter = package / "CodexCompanion.exe"
        shutil.copy2(launcher, starter)
        assert subprocess.run([str(starter), "--check-layout"], cwd=base).returncode == 4
        binary = package / "app/bin"; binary.mkdir(parents=True)
        source = base / "fixture.c"; source.write_text(FIXTURE, encoding="ascii")
        subprocess.run(["gcc", "-Os", "-mwindows", "-static-libgcc", str(source), "-o", str(binary / "CodexCompanion.exe")], check=True)
        assert subprocess.run([str(starter), "--check-layout"], cwd=base).returncode == 0
        assert subprocess.run([str(starter), "--invalid"], cwd=base).returncode == 2
        relocated = base / "relocated package"
        assert package.resolve().is_relative_to(base) and relocated.resolve().is_relative_to(base)
        package.rename(relocated)
        starter = relocated / "CodexCompanion.exe"; binary = relocated / "app/bin"
        child = subprocess.Popen([str(starter)], cwd=base)
        assert child.wait(timeout=5) == 0
        proof = binary / "launch-proof.txt"
        for _ in range(100):
            if proof.exists(): break
            time.sleep(.02)
        assert proof.read_text(encoding="ascii") == str(binary)
    print("PASS native launcher: missing layout, invalid argument, English spaces, relocation, independent working directory, transient process")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("launcher")
    run(Path(parser.parse_args().launcher).resolve())
