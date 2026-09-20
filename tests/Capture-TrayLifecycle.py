"""Capture the native tray fixture at approximately 60 Hz (requires Pillow)."""
import argparse
import ctypes as C
from ctypes import wintypes as W
import json
from pathlib import Path
import re
import subprocess
import threading
import time
from PIL import Image, ImageDraw


def capture_region(process, region, parent):
    x, y, width, height = region
    user, gdi = C.windll.user32, C.windll.gdi32
    user.IsWindowVisible.argtypes = [W.HWND]
    user.GetDC.restype = W.HDC
    user.ReleaseDC.argtypes = [W.HWND, W.HDC]
    gdi.CreateCompatibleDC.argtypes = [W.HDC]
    gdi.CreateCompatibleDC.restype = W.HDC
    gdi.SelectObject.argtypes = [W.HDC, W.HGDIOBJ]
    gdi.SelectObject.restype = W.HGDIOBJ
    gdi.BitBlt.argtypes = [W.HDC, C.c_int, C.c_int, C.c_int, C.c_int, W.HDC, C.c_int, C.c_int, W.DWORD]
    gdi.DeleteObject.argtypes = [W.HGDIOBJ]
    gdi.DeleteDC.argtypes = [W.HDC]

    class Header(C.Structure):
        _fields_ = [("size", W.DWORD), ("width", W.LONG), ("height", W.LONG),
                    ("planes", W.WORD), ("bits", W.WORD), ("compression", W.DWORD),
                    ("imagesize", W.DWORD), ("xppm", W.LONG), ("yppm", W.LONG),
                    ("used", W.DWORD), ("important", W.DWORD)]

    gdi.CreateDIBSection.argtypes = [W.HDC, C.c_void_p, W.UINT, C.POINTER(C.c_void_p), W.HANDLE, W.DWORD]
    gdi.CreateDIBSection.restype = W.HBITMAP
    dc = user.GetDC(None)
    memory = gdi.CreateCompatibleDC(dc)
    header = Header(C.sizeof(Header), width, -height, 1, 32, 0, 0, 0, 0, 0, 0)
    bits = C.c_void_p()
    bitmap = gdi.CreateDIBSection(dc, C.byref(header), 0, C.byref(bits), None, 0)
    if not bitmap:
        gdi.DeleteDC(memory)
        user.ReleaseDC(None, dc)
        raise RuntimeError("CreateDIBSection failed")
    previous = gdi.SelectObject(memory, bitmap)
    frames = []
    start = time.perf_counter()
    try:
        while process.poll() is None and time.perf_counter() - start < 30:
            tick = time.perf_counter()
            if not user.IsWindowVisible(parent):
                break
            if not gdi.BitBlt(memory, 0, 0, width, height, dc, x, y, 0x40CC0020):
                raise RuntimeError("BitBlt failed")
            pixels = C.string_at(bits.value, width * height * 4)
            frames.append((tick - start, Image.frombytes("RGB", (width, height), pixels, "raw", "BGRX")))
            time.sleep(max(0, 1 / 60 - (time.perf_counter() - tick)))
    finally:
        gdi.SelectObject(memory, previous)
        gdi.DeleteObject(bitmap)
        gdi.DeleteDC(memory)
        user.ReleaseDC(None, dc)
    return frames


def save_frames(frames, output, right=False):
    selected, next_time, transitions = [], 0, []
    for index, (elapsed, frame) in enumerate(frames):
        frame.save(output / f"frame-{index:03}.png")
        # Probe cells are teal. This scan line excludes the taskbar's own icons
        # and records transient centering or a white span during early edits.
        if (right or elapsed < 3.1) and frame.height > 20 and frame.width >= 220:
            start, end = (0, frame.width) if right else (8, 220)
            row = [frame.getpixel((x, 20)) for x in range(start, end)]
            teal = [x + start for x, (r, g, b) in enumerate(row)
                    if r < 65 and g > 85 and b > 85 and abs(g - b) < 40]
            state = [min(teal) if teal else -1, len(teal), sum(min(rgb) > 220 for rgb in row)]
            if not transitions or transitions[-1][2] != state:
                transitions.append([index, elapsed, state])
        if elapsed >= next_time:
            tile = Image.new("RGB", (400, 55), "#555")
            small = frame.copy()
            small.thumbnail((400, 30))
            tile.paste(small, (0, 22))
            ImageDraw.Draw(tile).text((3, 4), f"{index}: {elapsed:.3f}s", fill="white")
            selected.append(tile)
            next_time += 0.2
    sheet = Image.new("RGB", (1200, max(1, (len(selected) + 2) // 3) * 55), "#333")
    for index, tile in enumerate(selected):
        sheet.paste(tile, ((index % 3) * 400, (index // 3) * 55))
    sheet.save(output / "contact.png")
    (output / "times.json").write_text(json.dumps([t for t, _ in frames]))
    (output / "scanline.json").write_text(json.dumps(transitions, indent=2))
    print("Early scan line [frame, seconds, [first teal x, teal pixels, white pixels]]:", transitions)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--taskbar", action="store_true")
    parser.add_argument("--right", action="store_true")
    parser.add_argument("--secondary-taskbar", type=int)
    parser.add_argument("--exe", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    default = Path(__file__).resolve().parent / "Fixtures/TrayLifecycle/bin/x64/Debug/net10.0-windows10.0.22621.0/Pagurian.exe"
    exe = (args.exe or default).resolve()
    if args.taskbar and (args.right or args.secondary_taskbar):
        parser.error("--taskbar is the left fixture; choose --right or --secondary-taskbar for right placement")
    output = args.output or exe.parent / (
        f"capture-secondary-{args.secondary_taskbar}" if args.secondary_taskbar else
        "capture-right" if args.right else "capture-taskbar" if args.taskbar else "capture-isolated")
    output.mkdir(parents=True, exist_ok=True)
    command = [str(exe)] + (["--taskbar"] if args.taskbar else [])
    if args.secondary_taskbar:
        command += ["--secondary-taskbar", str(args.secondary_taskbar)]
    elif args.right:
        command += ["--right"]
    with subprocess.Popen(command, cwd=exe.parent, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          text=True, creationflags=0x08000000) as process:
        first = process.stdout.readline()
        match = re.search(r"Capture region: (-?\d+),(-?\d+),(\d+),(\d+) hwnd=(\d+)", first)
        if not match:
            process.kill()
            raise RuntimeError("Fixture did not report a capture region: " + first)
        print(first.strip())
        stdout, stderr = [], []
        readers = [threading.Thread(target=lambda: stdout.extend(process.stdout.readlines())),
                   threading.Thread(target=lambda: stderr.extend(process.stderr.readlines()))]
        for reader in readers:
            reader.start()
        try:
            region = tuple(map(int, match.groups()[:4]))
            frames = capture_region(process, region, int(match.group(5)))
            process.wait(timeout=5)
        except Exception:
            process.kill()
            process.wait()
            raise
        finally:
            for reader in readers:
                reader.join(timeout=5)
        log = first + "".join(stdout + stderr)
        (output / "run.log").write_text(log)
        print(log)
        save_frames(frames, output, args.right or bool(args.secondary_taskbar))
        print(f"Captured {len(frames)} frames: {output / 'contact.png'}")
        return process.returncode


if __name__ == "__main__":
    raise SystemExit(main())
