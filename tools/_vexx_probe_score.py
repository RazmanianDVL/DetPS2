import struct, zlib, re
iso = r"C:/Users/user/Downloads/Vexx(USA).iso"

def crc32(s):
    return zlib.crc32(s.encode("latin1")) & 0xffffffff

with open(iso, "rb") as f:
    f.seek(16 * 2048)
    pvd = f.read(2048)
    root_lba = struct.unpack_from("<I", pvd, 158)[0]
    root_size = struct.unpack_from("<I", pvd, 166)[0]
    f.seek(root_lba * 2048)
    data = f.read(root_size)
    i = 0
    stree = None
    while i < len(data):
        le = data[i]
        if le == 0:
            i = (i // 2048 + 1) * 2048
            continue
        name = data[i + 33 : i + 33 + data[i + 32]]
        lba = struct.unpack_from("<I", data, i + 2)[0]
        sz = struct.unpack_from("<I", data, i + 10)[0]
        if b"STREE0" in name.upper():
            stree = (lba, sz)
        i += le
    lba, sz = stree
    f.seek(lba * 2048)
    count = struct.unpack("<I", f.read(4))[0]
    idx = f.read(count * 24)
    tocEnd = 4 + count * 24
    crcs = {}
    for e in range(count):
        w = struct.unpack_from("<IIIIII", idx, e * 24)
        ncrc, off, size = w[2], w[4], w[5]
        if ncrc and 8 <= size <= 32 * 1024 * 1024 and tocEnd <= off < sz and off + size <= sz:
            crcs[ncrc] = (off, size)
    words = len(idx) // 4
    for i in range(words - 3):
        w0, w1, w2, w3 = struct.unpack_from("<IIII", idx, i * 4)
        for ncrc, off, size in [(w0, w2, w3), (w2, w0, w1)]:
            if ncrc and 8 <= size <= 32 * 1024 * 1024 and tocEnd <= off < sz and off + size <= sz:
                if ncrc not in crcs:
                    crcs[ncrc] = (off, size)

    for leaf in [
        "loadtimer_w-alpha.tgax",
        "loadtimer_light_nm.tgax",
        "loadtimer_w_alpha.tgax",
        "loadtimer_light.tgax",
    ]:
        for pref in [
            r"data\textures\onscreengraphics\fonts\\",
            r"data\textures\onscreengraphics\\",
            r"data\textures\frontend\\",
            r"data\textures\hud\\",
            r"data\fonts\\",
            r"data\textures\onscreengraphics\loadtimer\\",
            r"data\textures\frontend\load\\",
        ]:
            p = (pref + leaf).replace("\\\\", "\\")
            c = crc32(p)
            if c in crcs:
                print("HIT", p, crcs[c])

    print("scan timer...")
    n = 0
    pat = re.compile(rb"[A-Za-z0-9_./\\:-]*timer[A-Za-z0-9_./\\-]*\.tgax", re.I)
    for ncrc, (off, size) in crcs.items():
        if size > 100000 or size < 20:
            continue
        f.seek(lba * 2048 + off)
        blob = f.read(min(size, 8192))
        if b"timer" in blob.lower():
            for m in pat.finditer(blob):
                print(m.group(0).decode("latin1", "replace"))
                n += 1
                if n > 40:
                    break
        if n > 40:
            break
    print("done n", n)

    def score(b):
        if len(b) < 4:
            return -1, 0, 0
        w0 = int.from_bytes(b[:4], "little")
        printable = sum(1 for c in b[:32] if 32 <= c < 127 or c in (9, 10, 13))
        sc = printable // 5
        if printable <= 6 and w0:
            sc += 10
        elif printable <= 12 and w0:
            sc += 4
        return sc, printable, w0

    for p in [
        r"data\textures\onscreengraphics\fonts\button2.tgax",
        r"data\textures\onscreengraphics\fonts\button3.tgax",
        r"data\textures\onscreengraphics\fonts\button4.tgax",
        r"data\textures\onscreengraphics\fonts\button9.tgax",
        r"data\textures\environment\shadowcircle_nc.tgax",
        r"data\textures\onscreengraphics\fonts\button1.tgax",
        r"data\textures\onscreengraphics\fonts\button5.tgax",
    ]:
        off, size = crcs[crc32(p)]
        f.seek(lba * 2048 + off)
        b = f.read(min(96, size))
        print(p.split("\\")[-1], "sz", size, "score", score(b))
