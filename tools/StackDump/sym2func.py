# -*- coding: utf-8 -*-
"""把 StackDump 输出的 `模块+0x偏移` 换算成函数名。

原理：MinGW 链接出的 exe 默认保留 COFF 符号表，符号的 value 是相对所在节的偏移，
加上该节的 VirtualAddress 就是 RVA。StackDump 打印的偏移正好是 RVA，
所以取“不大于该 RVA 的最近一个函数符号”即可。

用法：
    python sym2func.py <exe 路径>              # 交互：逐行输入 0x5602
    python sym2func.py <exe 路径> stack.txt    # 直接把 dump 文件里的偏移全部标注
"""
import struct
import sys


def load_symbols(path):
    data = open(path, 'rb').read()
    if data[:2] != b'MZ':
        raise SystemExit('不是 PE 文件')
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        raise SystemExit('PE 签名不正确')
    coff = pe + 4
    num_sections, = struct.unpack_from('<H', data, coff + 2)
    ptr_sym, num_sym = struct.unpack_from('<II', data, coff + 8)
    opt_size, = struct.unpack_from('<H', data, coff + 16)

    if ptr_sym == 0 or num_sym == 0:
        raise SystemExit('该 exe 没有 COFF 符号表（可能被 strip 过）')

    sections = []
    sec_off = coff + 20 + opt_size
    for i in range(num_sections):
        base = sec_off + i * 40
        vsize, vaddr, rawsize, rawptr = struct.unpack_from('<IIII', data, base + 8)
        sections.append({'va': vaddr, 'vs': vsize, 'raw': rawptr})

    syms = []
    off = ptr_sym
    for _ in range(num_sym):
        name_raw = data[off:off + 8]
        value, sec_no, typ, storage, naux = struct.unpack_from('<IhHBB', data, off + 8)
        aux = naux
        off += 18 + aux * 18
        if sec_no <= 0 or sec_no > len(sections):
            continue
        if not (typ & 0x20):          # 0x20 = 函数
            continue
        va = sections[sec_no - 1]['va'] + value

        if name_raw[:4] == b'\0\0\0\0':
            str_off, = struct.unpack_from('<I', data, ptr_sym + num_sym * 18 + struct.unpack_from('<I', name_raw, 4)[0])
            end = data.index(b'\0', str_off)
            name = data[str_off:end].decode('ascii', 'replace')
        else:
            name = name_raw.rstrip(b'\0').decode('ascii', 'replace')
        syms.append((va, name))

    syms.sort()
    return syms


def resolve(syms, rva):
    lo, hi = 0, len(syms) - 1
    best = None
    while lo <= hi:
        mid = (lo + hi) // 2
        if syms[mid][0] <= rva:
            best = syms[mid]
            lo = mid + 1
        else:
            hi = mid - 1
    if not best:
        return '<未知>'
    return '%s+0x%x' % (best[1], rva - best[0])


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    syms = load_symbols(sys.argv[1])
    print('# 已载入 %d 个函数符号' % len(syms))

    if len(sys.argv) > 2:
        import re
        with open(sys.argv[2], 'r', encoding='utf-8', errors='replace') as f:
            for line in f:
                m = re.search(r'udp-sender\.exe\+0x([0-9a-fA-F]+)', line)
                if m:
                    rva = int(m.group(1), 16)
                    print(line.rstrip() + '    <<< ' + resolve(syms, rva))
                else:
                    print(line.rstrip())
        return 0

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            rva = int(line, 16)
        except ValueError:
            continue
        print('0x%x -> %s' % (rva, resolve(syms, rva)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
