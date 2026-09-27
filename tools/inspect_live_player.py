"""Read the player pointer captured by an already-installed trainer hook.

Development helper only. It opens the game with read-only access and never
installs hooks or writes process memory.
"""

from __future__ import annotations

import argparse
import ctypes
import struct

import pefile


PROCESS_VM_READ = 0x0010
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PLAYER_HOOK_OFFSET = 9
PLAYER_CODE_OFFSET = 0x10
PLAYER_PROBE_PATTERN = (
    "48 83 EC 68 48 85 C9 74 0F ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? "
    "48 8D 15 ?? ?? ?? ?? 48 8D 4C 24 38"
)


def parse_pattern(pattern: str) -> list[int | None]:
    return [None if value == "??" else int(value, 16) for value in pattern.split()]


def find_unique(data: bytes, pattern: list[int | None]) -> int:
    matches: list[int] = []
    first = next(index for index, value in enumerate(pattern) if value is not None)
    needle = bytes((pattern[first],))
    offset = 0
    while True:
        offset = data.find(needle, offset)
        if offset < 0:
            break
        start = offset - first
        if start >= 0 and all(value is None or data[start + index] == value for index, value in enumerate(pattern)):
            matches.append(start)
        offset += 1

    if len(matches) != 1:
        raise RuntimeError(f"expected one player signature, found {len(matches)}")
    return matches[0]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("executable")
    parser.add_argument("process_id", type=int)
    parser.add_argument("module_base", type=lambda value: int(value, 0))
    args = parser.parse_args()

    image = open(args.executable, "rb").read()
    pe = pefile.PE(data=image, fast_load=True)
    signature_file_offset = find_unique(image, parse_pattern(PLAYER_PROBE_PATTERN))
    hook_rva = pe.get_rva_from_offset(signature_file_offset) + PLAYER_HOOK_OFFSET
    hook_address = args.module_base + hook_rva

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.ReadProcessMemory.argtypes = (
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    )
    handle = kernel32.OpenProcess(
        PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION,
        False,
        args.process_id,
    )
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())

    def read(address: int, size: int) -> bytes:
        buffer = ctypes.create_string_buffer(size)
        count = ctypes.c_size_t()
        if not kernel32.ReadProcessMemory(handle, address, buffer, size, ctypes.byref(count)) or count.value != size:
            raise ctypes.WinError(ctypes.get_last_error())
        return buffer.raw

    try:
        jump = read(hook_address, 15)
        if jump[:6] != b"\xff\x25\x00\x00\x00\x00":
            raise RuntimeError("the trainer player hook is not currently installed")
        player_code_address = struct.unpack_from("<Q", jump, 6)[0]
        storage_address = player_code_address - PLAYER_CODE_OFFSET
        player_address = struct.unpack("<Q", read(storage_address, 8))[0]
        if player_address < 0x10000:
            raise RuntimeError("the installed hook has not captured a player yet")

        print(f"player=0x{player_address:X}")
        for base_offset in (0x154, 0x17C, 0x1CC, 0x1F4, 0x698):
            values = struct.unpack("<10f", read(player_address + base_offset, 10 * 4))
            print(f"resource_set=player+0x{base_offset:X}")
            for index, value in enumerate(values):
                print(f"  [{index:02}] +0x{base_offset + index * 4:03X} = {value:g}")
    finally:
        kernel32.CloseHandle(handle)


if __name__ == "__main__":
    main()
