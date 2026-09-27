"""Locate Victory Point SCAR bindings in a local RelicCoH3.exe.

Development helper only. It performs static analysis and never attaches to the game.
Requires pefile and capstone on PYTHONPATH.
"""

from __future__ import annotations

import argparse
import re
import struct

import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs


TERMS = (
    b"Player_GetNumVictoryPoints",
    b"World_GetNumVictoryPoints",
    b"GE_TickerValuesUpdated",
    b"local_player_victory_points",
    b"VictoryPoints",
)


def rip_relative_lea_references(code: bytes, code_va: int, target_va: int):
    offset = 0
    while True:
        offset = code.find(b"\x8d", offset)
        if offset < 0:
            return

        # REX + LEA reg,[RIP+disp32]
        if offset >= 1 and 0x40 <= code[offset - 1] <= 0x4F and offset + 6 <= len(code):
            modrm = code[offset + 1]
            if modrm & 0xC7 == 0x05:
                instruction_start = offset - 1
                instruction_end = offset + 6
                displacement = struct.unpack_from("<i", code, offset + 2)[0]
                if code_va + instruction_end + displacement == target_va:
                    yield instruction_start

        offset += 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("executable")
    parser.add_argument(
        "--function-at",
        type=lambda value: int(value, 0),
        help="Disassemble the function containing this RVA instead of scanning strings.",
    )
    parser.add_argument(
        "--term",
        action="append",
        help="Scan only for this null-terminated ASCII string (may be repeated).",
    )
    args = parser.parse_args()

    image = open(args.executable, "rb").read()
    pe = pefile.PE(data=image, fast_load=True)
    image_base = pe.OPTIONAL_HEADER.ImageBase
    text = next(section for section in pe.sections if section.Name.rstrip(b"\0") == b".text")
    text_data = text.get_data()
    text_va = image_base + text.VirtualAddress
    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    disassembler.skipdata = True

    if args.function_at is not None:
        offset = args.function_at - text.VirtualAddress
        if offset < 0 or offset >= len(text_data):
            raise SystemExit("RVA is outside the .text section")

        separator = b"\xCC" * 4
        previous_separator = text_data.rfind(separator, 0, offset)
        next_separator = text_data.find(separator, offset)
        start = previous_separator + len(separator) if previous_separator >= 0 else 0
        while start < len(text_data) and text_data[start] == 0xCC:
            start += 1
        end = next_separator if next_separator >= 0 else len(text_data)
        for instruction in disassembler.disasm(text_data[start:end], text_va + start):
            marker = "=>" if instruction.address - image_base == args.function_at else "  "
            print(f"{marker} {instruction.address - image_base:08X}  {instruction.mnemonic:8} {instruction.op_str}")
        return

    terms = tuple(term.encode() for term in args.term) if args.term else TERMS
    for term in terms:
        for match in re.finditer(re.escape(term) + b"\0", image):
            string_rva = pe.get_rva_from_offset(match.start())
            string_va = image_base + string_rva
            references = list(rip_relative_lea_references(text_data, text_va, string_va))
            print(f"{term.decode()}: file=0x{match.start():X} rva=0x{string_rva:X}")
            for reference in references:
                start = max(0, reference - 48)
                end = min(len(text_data), reference + 160)
                print(f"  xref rva=0x{text.VirtualAddress + reference:X}")
                for instruction in disassembler.disasm(text_data[start:end], text_va + start):
                    marker = "=>" if instruction.address == text_va + reference else "  "
                    print(f"  {marker} {instruction.address - image_base:08X}  {instruction.mnemonic:8} {instruction.op_str}")


if __name__ == "__main__":
    main()
