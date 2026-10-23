#!/usr/bin/env python3
"""Generate the hand-written PDF fixtures for the Comprehension PDF tests.

usage: python3 -I generate-handwritten.py <output-dir> [--large]

Needs the `cryptography` package (AES fixtures only). Every byte is deterministic: no timestamps, fixed
document ids, fixed IVs, a seeded random source. `truncated.pdf` is cut from `cups-text.pdf`, so produce
that file first (see README.md). `--large` additionally writes the hostile fixtures that are larger than
the 1 MiB attachment intake ceiling; those are never committed.
"""
import hashlib
import os
import random
import struct
import sys
import zlib

OUT = sys.argv[1]
LARGE = "--large" in sys.argv[2:]
os.makedirs(OUT, exist_ok=True)

HEADER = b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n"


def write(name, data):
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(data)
    print(f"{name}: {len(data)} bytes")


def build(objs, root=1, trailer_extra=b"", header=HEADER, startxref_override=None, xref_shift=0):
    out = bytearray(header)
    offsets = {}
    for n in sorted(objs):
        offsets[n] = len(out)
        out += b"%d 0 obj\n" % n + objs[n] + b"\nendobj\n"
    xref = len(out)
    size = max(objs) + 1
    out += b"xref\n0 %d\n" % size
    out += b"0000000000 65535 f \n"
    for n in range(1, size):
        if n in offsets:
            out += b"%010d 00000 n \n" % (offsets[n] + xref_shift)
        else:
            out += b"0000000000 65535 f \n"
    start = xref if startxref_override is None else startxref_override
    out += b"trailer\n<< /Size %d /Root %d 0 R %s >>\nstartxref\n%d\n%%%%EOF\n" % (size, root, trailer_extra, start)
    return bytes(out)


def stream(entries, data):
    return b"<< /Length %d %s >>\nstream\n" % (len(data), entries) + data + b"\nendstream"


FONT = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"


def page_obj(contents, fonts=b"/F1 5 0 R", extra=b"", resources_extra=b""):
    return (
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents %s /Resources << /Font << %s >> %s >> %s >>"
        % (contents, fonts, resources_extra, extra)
    )


def simple_doc(content, enc=lambda n, d: d, extra_objs=None, page_extra=b"", catalog_extra=b"", trailer_extra=b"", content_entries=b""):
    objs = {
        1: b"<< /Type /Catalog /Pages 2 0 R %s >>" % catalog_extra,
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: page_obj(b"4 0 R", extra=page_extra),
        4: stream(content_entries, enc(4, content)),
        5: FONT,
    }
    if extra_objs:
        objs.update(extra_objs)
    return build(objs, trailer_extra=trailer_extra)


def show(text, y=700, size=18):
    return b"BT /F1 %d Tf 72 %d Td (%s) Tj ET\n" % (size, y, text)


# ---- ordinary text, several pages: page 2 has no content at all, the marker is on page 3
multi_objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 >>",
    3: page_obj(b"7 0 R", fonts=b"/F1 10 0 R"),
    4: b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 10 0 R >> >> >>",
    5: page_obj(b"8 0 R", fonts=b"/F1 10 0 R"),
    6: page_obj(b"9 0 R", fonts=b"/F1 10 0 R"),
    7: stream(b"", show(b"PAGE-ONE-ALPHA")),
    8: stream(b"", show(b"PAGE-THREE-MARKER-GAMMA")),
    9: stream(b"", show(b"PAGE-FOUR-DELTA")),
    10: FONT,
}
write("multi-page.pdf", build(multi_objs))

# ---- a page that draws one image and shows no text; a second page with no content
image = b"\xff\x00\x00\x00\xff\x00\x00\x00\xff\xff\xff\x00"  # 2x2 RGB
image_objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
    3: b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>",
    4: stream(b"", b"q 200 0 0 200 100 500 cm /Im1 Do Q\n"),
    5: stream(b"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", image),
    6: b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
}
write("image-only.pdf", build(image_objs))

# ---- three runs whose content-stream order differs from their position on the page (bottom first)
content_order = (
    show(b"FIRST-IN-STREAM-BOTTOM", y=100)
    + show(b"SECOND-IN-STREAM-TOP", y=700)
    + show(b"THIRD-IN-STREAM-MIDDLE", y=400)
)
write("content-order.pdf", simple_doc(content_order))

# ---- one run names a font the page does not define
missing_font = (
    b"BT /F2 18 Tf 72 700 Td (MISSING-FONT-RUN) Tj ET\n"
    + show(b"GOOD-FONT-RUN", y=650)
)
write("missing-font.pdf", simple_doc(missing_font))

# ---- a TrueType font that is not embedded and that no machine has installed. The text comes from the file, never
# from a font found on the host, so the result cannot depend on which fonts the worker's sandbox can read.
non_embedded = b"FixtureUnknownFont-Regular"
write(
    "non-embedded-font.pdf",
    build({
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: page_obj(b"4 0 R"),
        4: stream(b"", show(b"NON-EMBEDDED-FONT-TEXT")),
        5: b"<< /Type /Font /Subtype /TrueType /BaseFont /%s /FirstChar 32 /LastChar 126 /Widths [%s] /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>"
        % (non_embedded, b" ".join([b"556"] * 95)),
        6: b"<< /Type /FontDescriptor /FontName /%s /Flags 32 /FontBBox [-200 -200 1200 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 >>" % non_embedded,
    }),
)

# ---- a text layer made only of spaces
write("whitespace-only.pdf", simple_doc(b"BT /F1 18 Tf 72 700 Td (     ) Tj ET\n"))

# ---- hidden.pdf: visible, invisible (Tr 3), white-on-white, tiny, stroke-only, off-page
hidden_content = (
    b"BT /F1 18 Tf 0 Tr 72 700 Td (VISIBLE-LINE-ALPHA) Tj ET\n"
    b"BT /F1 18 Tf 3 Tr 72 650 Td (INVISIBLE-LINE-BRAVO) Tj ET\n"
    b"q 1 1 1 rg BT /F1 18 Tf 0 Tr 72 600 Td (WHITE-ON-WHITE-CHARLIE) Tj ET Q\n"
    b"BT /F1 0.5 Tf 0 Tr 72 550 Td (TINY-TEXT-DELTA) Tj ET\n"
    b"BT /F1 18 Tf 1 Tr 72 500 Td (STROKE-ONLY-FOXTROT) Tj ET\n"
    b"BT /F1 18 Tf 0 Tr -900 -900 Td (OFFPAGE-ECHO) Tj ET\n"
    b"BT /F1 18 Tf 0 Tr 72 450 Td (VISIBLE-LINE-GOLF) Tj ET\n"
)
write("hidden.pdf", simple_doc(hidden_content))

# ---- marked-content /ActualText: the replacement text is not a text-showing run
actual_content = (
    b"/Span << /ActualText (REPLACEMENT-NEVER-EXTRACTED) >> BDC\n"
    b"BT /F1 18 Tf 72 700 Td (SHOWN-GLYPHS-ARE-EXTRACTED) Tj ET\n"
    b"EMC\n"
)
write("actual-text.pdf", simple_doc(actual_content))

# ---- encryption
PAD = bytes.fromhex("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A")


def rc4(key, data):
    s = list(range(256))
    j = 0
    for i in range(256):
        j = (j + s[i] + key[i % len(key)]) & 255
        s[i], s[j] = s[j], s[i]
    i = j = 0
    out = bytearray()
    for b in data:
        i = (i + 1) & 255
        j = (j + s[i]) & 255
        s[i], s[j] = s[j], s[i]
        out.append(b ^ s[(s[i] + s[j]) & 255])
    return bytes(out)


def pad_pw(pw):
    return (pw + PAD)[:32]


ID0 = bytes.fromhex("00112233445566778899aabbccddeeff")
P_PERM = -4  # 0xFFFFFFFC


def enc_rc4_40(user_pw, owner_pw):
    # Algorithm 3 (R=2); an owner password of exactly b"" is kept as the empty owner password.
    h = hashlib.md5(pad_pw(owner_pw)).digest()
    o = rc4(h[:5], pad_pw(user_pw))
    # Algorithm 2 (R=2)
    key = hashlib.md5(pad_pw(user_pw) + o + struct.pack("<i", P_PERM) + ID0).digest()[:5]
    u = rc4(key, PAD)

    def encrypt(objnum, data):
        okey = hashlib.md5(key + struct.pack("<I", objnum)[:3] + b"\x00\x00").digest()[:10]
        return rc4(okey, data)

    enc_dict = b"<< /Filter /Standard /V 1 /R 2 /O <%s> /U <%s> /P %d >>" % (o.hex().encode(), u.hex().encode(), P_PERM)
    return key, encrypt, enc_dict


def enc_aes128(user_pw, owner_pw):
    # Algorithm 3 (R=4)
    h = hashlib.md5(pad_pw(owner_pw)).digest()
    for _ in range(50):
        h = hashlib.md5(h).digest()
    okey = h[:16]
    o = rc4(okey, pad_pw(user_pw))
    for i in range(1, 20):
        o = rc4(bytes(b ^ i for b in okey), o)
    # Algorithm 2 (R=4)
    k = hashlib.md5(pad_pw(user_pw) + o + struct.pack("<i", P_PERM) + ID0).digest()
    for _ in range(50):
        k = hashlib.md5(k[:16]).digest()
    key = k[:16]
    # Algorithm 5 (R>=3)
    u = hashlib.md5(PAD + ID0).digest()
    u = rc4(key, u)
    for i in range(1, 20):
        u = rc4(bytes(b ^ i for b in key), u)
    u = u + b"\x00" * 16

    from cryptography.hazmat.primitives import padding
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

    def encrypt(objnum, data):
        okey = hashlib.md5(key + struct.pack("<I", objnum)[:3] + b"\x00\x00" + b"sAlT").digest()[:16]
        iv = bytes(range(16))
        padder = padding.PKCS7(128).padder()
        padded = padder.update(data) + padder.finalize()
        enc = Cipher(algorithms.AES(okey), modes.CBC(iv)).encryptor()
        return iv + enc.update(padded) + enc.finalize()

    enc_dict = (
        b"<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /AuthEvent /DocOpen /CFM /AESV2 /Length 16 >> >> "
        b"/StmF /StdCF /StrF /StdCF /O <%s> /U <%s> /P %d >>" % (o.hex().encode(), u.hex().encode(), P_PERM)
    )
    return key, encrypt, enc_dict


enc_content = show(b"ENCRYPTED-CODEWORD-PAPAYA-4410")
trailer_id = b"/ID [<%s> <%s>]" % (ID0.hex().encode(), ID0.hex().encode())

for name, maker, upw, opw in [
    ("encrypted-rc4-empty.pdf", enc_rc4_40, b"", b"owner"),
    ("encrypted-rc4-password.pdf", enc_rc4_40, b"secret", b"owner"),
    ("encrypted-aes-empty.pdf", enc_aes128, b"", b"owner"),
    ("encrypted-aes-password.pdf", enc_aes128, b"secret", b"owner"),
    # user password required, but the OWNER password is the empty string
    ("encrypted-rc4-owner-empty.pdf", enc_rc4_40, b"secret", b""),
    ("encrypted-aes-owner-empty.pdf", enc_aes128, b"secret", b""),
]:
    _, encrypt, enc_dict = maker(upw, opw)
    write(name, simple_doc(enc_content, enc=encrypt, extra_objs={6: enc_dict}, trailer_extra=trailer_id + b" /Encrypt 6 0 R"))

# A file that is not encrypted, opens, and has a page the parser refuses (the /Contents chain of cyclic-ref-chain.pdf), with an
# unreferenced note whose text mentions the /Encrypt name. Only a failure to open can be an unsupported security handler.
write(
    "page-failure-mentions-encrypt.pdf",
    build({
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: page_obj(b"6 0 R"),
        5: FONT,
        6: b"7 0 R",
        7: b"6 0 R",
        8: b"<< /Note (the /Encrypt dictionary is described here) >>",
    }),
)

# Public-key (certificate) security is a handler PdfPig does not implement: the trailer names an /Encrypt dictionary with no
# /O or /U, which the parser reads as a Standard handler and fails on. The file is well formed, so it must not read as damaged.
pubsec_dict = (
    b"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /R 4 /Length 128 /Recipients [<3082>] "
    b"/CF << /DefaultCryptFilter << /CFM /AESV2 /Length 16 /Recipients [<3082>] >> >> /StmF /DefaultCryptFilter /StrF /DefaultCryptFilter >>"
)
write("encrypted-pubsec.pdf", simple_doc(enc_content, extra_objs={6: pubsec_dict}, trailer_extra=trailer_id + b" /Encrypt 6 0 R"))

# ---- cyclic structures
# Pages tree whose Kids contains its own parent (2 -> 3 -> 2).
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Parent 3 0 R /Kids [3 0 R] /Count 1 >>",
    3: b"<< /Type /Pages /Parent 2 0 R /Kids [2 0 R] /Count 1 >>",
}
write("cyclic-pages.pdf", build(objs))

# Pages node that lists itself as its own kid.
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Parent 2 0 R /Kids [2 0 R] /Count 1 >>",
}
write("cyclic-pages-self.pdf", build(objs))

# Cycle with a real page reachable too: Pages -> [Pages(self), Page]
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Parent 2 0 R /Kids [2 0 R 3 0 R] /Count 2 >>",
    3: page_obj(b"4 0 R"),
    4: stream(b"", show(b"PAGE-BESIDE-CYCLE")),
    5: FONT,
}
write("cyclic-pages-with-page.pdf", build(objs))

# Self-referencing indirect object chain: /Contents 6 0 R, 6 0 obj = 7 0 R, 7 0 obj = 6 0 R.
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"6 0 R"),
    5: FONT,
    6: b"7 0 R",
    7: b"6 0 R",
}
write("cyclic-ref-chain.pdf", build(objs))

# Stream whose /Length refers to an object that refers to itself.
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"4 0 R"),
    4: b"<< /Length 6 0 R >>\nstream\nBT /F1 18 Tf 72 700 Td (LENGTH-CYCLE) Tj ET\nendstream",
    5: FONT,
    6: b"6 0 R",
}
write("cyclic-length.pdf", build(objs))

# Form XObject that invokes itself (and whose resources contain itself).
form = b"/Fm1 Do\nBT /F1 18 Tf 72 650 Td (INSIDE-FORM) Tj ET\n"
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"4 0 R", resources_extra=b"/XObject << /Fm1 6 0 R >>"),
    4: stream(b"", show(b"BEFORE-FORM") + b"/Fm1 Do\n"),
    5: FONT,
    6: stream(b"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /Fm1 6 0 R >> >>", form),
}
write("cyclic-xobject.pdf", build(objs))

# Two forms that invoke each other (indirect cycle A -> B -> A).
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"4 0 R", resources_extra=b"/XObject << /FmA 6 0 R >>"),
    4: stream(b"", show(b"BEFORE-AB") + b"/FmA Do\n"),
    5: FONT,
    6: stream(b"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /FmB 7 0 R >> >>", b"/FmB Do\nBT /F1 18 Tf 72 650 Td (INSIDE-A) Tj ET\n"),
    7: stream(b"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /FmA 6 0 R >> >>", b"/FmA Do\nBT /F1 18 Tf 72 600 Td (INSIDE-B) Tj ET\n"),
}
write("cyclic-xobject-ab.pdf", build(objs))


# ---- deep nesting
def deep_pages(depth):
    objs = {1: b"<< /Type /Catalog /Pages 2 0 R >>"}
    for i in range(depth):
        n = 2 + i
        objs[n] = b"<< /Type /Pages %s /Kids [%d 0 R] /Count 1 >>" % (b"/Parent %d 0 R" % (n - 1) if i else b"", n + 1)
    leaf = 2 + depth
    objs[leaf] = b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Contents %d 0 R /Resources << /Font << /F1 %d 0 R >> >> >>" % (leaf - 1, leaf + 1, leaf + 2)
    objs[leaf + 1] = stream(b"", show(b"DEEP-PAGE-LEAF"))
    objs[leaf + 2] = FONT
    return build(objs)


write("deep-pages-1k.pdf", deep_pages(1000))

# Deeply nested arrays inside the page content stream.
depth = 200000
content = show(b"BEFORE-DEEP-ARRAY") + b"[" * depth + b"]" * depth + b"\n" + show(b"AFTER-DEEP-ARRAY", y=650)
write("deep-array-content.pdf", simple_doc(content))

# Page tree claiming two billion pages (only one is real).
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 2000000000 >>",
    3: page_obj(b"4 0 R"),
    4: stream(b"", show(b"REAL-PAGE-ONE")),
    5: FONT,
}
write("huge-count.pdf", build(objs))

# ---- predefined CJK CMap (90ms-RKSJ-H) over a non-embedded CID font: PdfPig's own embedded CMap resources
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"4 0 R"),
    4: stream(b"", b"BT /F1 18 Tf 72 700 Td <82A082A282A4> Tj ET\n"),
    5: b"<< /Type /Font /Subtype /Type0 /BaseFont /MS-Gothic /Encoding /90ms-RKSJ-H /DescendantFonts [6 0 R] >>",
    6: b"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /MS-Gothic /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> /DW 1000 /FontDescriptor 7 0 R >>",
    7: b"<< /Type /FontDescriptor /FontName /MS-Gothic /Flags 4 /FontBBox [0 -137 1000 859] /ItalicAngle 0 /Ascent 859 /Descent -137 /CapHeight 700 /StemV 80 >>",
}
write("cjk-predefined-cmap.pdf", build(objs))


# ---- Identity-H Type0 fonts whose text comes only from a ToUnicode CMap
def to_unicode_cmap(mapping):
    lines = [
        b"/CIDInit /ProcSet findresource begin",
        b"12 dict begin",
        b"begincmap",
        b"/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def",
        b"/CMapName /Adobe-Identity-UCS def",
        b"/CMapType 2 def",
        b"1 begincodespacerange",
        b"<0000> <FFFF>",
        b"endcodespacerange",
        b"%d beginbfchar" % len(mapping),
    ]
    for code, text in mapping:
        lines.append(b"<%04X> <%s>" % (code, text.encode("utf-16-be").hex().upper().encode()))
    lines += [b"endbfchar", b"endcmap", b"CMapName currentdict /CMap defineresource pop", b"end", b"end"]
    return b"\n".join(lines) + b"\n"


def type0_doc(runs, mapping):
    """runs: list of (y, [codes]) shown with the Identity-H font; mapping: [(code, unicode text)]"""
    content = b"".join(
        b"BT /F1 18 Tf 72 %d Td <%s> Tj ET\n" % (y, b"".join(b"%04X" % c for c in codes)) for y, codes in runs
    )
    objs = {
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: page_obj(b"4 0 R"),
        4: stream(b"", content),
        5: b"<< /Type /Font /Subtype /Type0 /BaseFont /FixtureSans /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>",
        6: b"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /FixtureSans /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /CIDToGIDMap /Identity /FontDescriptor 7 0 R >>",
        7: b"<< /Type /FontDescriptor /FontName /FixtureSans /Flags 4 /FontBBox [0 -200 1000 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        8: stream(b"", to_unicode_cmap(mapping)),
    }
    return build(objs)


# "AB" + U+1F600 (a surrogate pair in UTF-16) + "CD"
write(
    "astral-plane.pdf",
    type0_doc(
        [(700, [1, 2, 3, 4, 5])],
        [(1, "A"), (2, "B"), (3, "\U0001F600"), (4, "C"), (5, "D")],
    ),
)

# Page 1 is "AB" + U+1F600 + "CD" (five glyphs), page 2 is "EFGHIJKL" (eight glyphs), page 3 has no content. A ceiling that
# cuts page 1 inside the pair must stop there: page 2 is never read, which a per-page glyph bound of 7 would notice.
astral_multi_objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R 5 0 R 4 0 R] /Count 3 >>",
    3: page_obj(b"6 0 R"),
    4: b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
    5: page_obj(b"7 0 R"),
    6: stream(b"", b"BT /F1 18 Tf 72 700 Td <00010002000300040005> Tj ET\n"),
    7: stream(b"", b"BT /F1 18 Tf 72 700 Td <0006000700080009000A000B000C000D> Tj ET\n"),
    8: stream(b"", to_unicode_cmap([(1, "A"), (2, "B"), (3, "\U0001F600"), (4, "C"), (5, "D"), (6, "E"), (7, "F"), (8, "G"), (9, "H"), (10, "I"), (11, "J"), (12, "K"), (13, "L")])),
    9: b"<< /Type /Font /Subtype /Type0 /BaseFont /FixtureSans /Encoding /Identity-H /DescendantFonts [10 0 R] /ToUnicode 8 0 R >>",
    10: b"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /FixtureSans /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /CIDToGIDMap /Identity /FontDescriptor 11 0 R >>",
    11: b"<< /Type /FontDescriptor /FontName /FixtureSans /Flags 4 /FontBBox [0 -200 1000 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
}
astral_multi = build(astral_multi_objs)
write("astral-multi-page.pdf", astral_multi.replace(b"/F1 5 0 R", b"/F1 9 0 R"))

# Two lines of CJK, one of them with a U+20BB7 (also astral)
write(
    "cjk-tounicode.pdf",
    type0_doc(
        [(700, [1, 2, 3]), (650, [4, 5]), (600, [6, 7, 8])],
        [(1, "日"), (2, "本"), (3, "語"), (4, "漢"), (5, "字"), (6, "\U00020BB7"), (7, "野"), (8, "家")],
    ),
)

# Control characters a ToUnicode CMap can map a glyph to: NUL and form feed must never reach the output
write(
    "control-characters.pdf",
    type0_doc(
        [(700, [1, 2, 3, 4, 5])],
        [(1, "A"), (2, "\u0000"), (3, "\u000c"), (4, "B"), (5, "C")],
    ),
)

# ---- text laid out by hand: Helvetica runs placed with Tm, so the order the content stream draws them in is chosen
# Each pair of runs is on one baseline. A text extractor that only looks at the gap to the right glues runs that are drawn
# right to left, or on top of each other, into one word.
same_line = (
    b"BT /F1 12 Tf 1 0 0 1 400 700 Tm (RIGHTWORD) Tj 1 0 0 1 72 700 Tm (LEFTWORD) Tj ET\n"  # right run first, far apart
    b"BT /F1 10 Tf 1 0 0 1 520 650 Tm (17) Tj 1 0 0 1 72 650 Tm (Annual Report) Tj ET\n"  # page number first, then the title
    b"BT /F1 12 Tf 1 0 0 1 72 600 Tm [(WA) 150 (VE)] TJ ET\n"  # a kerned pair is one word: the control
    b"BT /F1 12 Tf 1 0 0 1 72 550 Tm (FIRSTRUN) Tj 1 0 0 1 72 550 Tm (SECONDRUN) Tj ET\n"  # the second run starts on the first
    b"BT /F1 12 Tf 1 0 0 1 72 500 Tm (M) Tj 1 0 0 1 82.55 503 Tm (^) Tj 1 0 0 1 82 500 Tm (uller) Tj ET\n"  # an accent drawn over the letter after it
)
write("same-line-order.pdf", simple_doc(same_line))


# ---- glyphs placed one by one on an Identity-H font whose every glyph is 1 em (18 pt) wide and maps to its character
# through a ToUnicode CMap, so a line's geometry is exact: glyphs placed 18 pt apart touch.
def placed_doc(lines):
    """lines: [(y, [(x, character)])] in the order the content stream draws them."""
    characters = sorted({character for _, glyphs in lines for _, character in glyphs})
    codes = {character: index + 1 for index, character in enumerate(characters)}
    content = b"BT /F1 18 Tf\n" + b"".join(
        b"1 0 0 1 %d %d Tm <%04X> Tj\n" % (x, y, codes[character]) for y, glyphs in lines for x, character in glyphs
    ) + b"ET\n"
    objs = {
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: page_obj(b"4 0 R"),
        4: stream(b"", content),
        5: b"<< /Type /Font /Subtype /Type0 /BaseFont /FixtureSans /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>",
        6: b"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /FixtureSans /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /CIDToGIDMap /Identity /FontDescriptor 7 0 R >>",
        7: b"<< /Type /FontDescriptor /FontName /FixtureSans /Flags 4 /FontBBox [0 -200 1000 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        8: stream(b"", to_unicode_cmap([(code, character) for character, code in codes.items()])),
    }
    return build(objs)


def left_to_right(y, text, x=72):
    """The characters in the order given, each 18 pt right of the one before (how a producer writes visual order)."""
    return (y, [(x + 18 * index, character) for index, character in enumerate(text)])


def right_to_left(y, text, x=540):
    """The characters in the order given, each 18 pt left of the one before (how a producer writes logical order)."""
    return (y, [(x - 18 * (index + 1), character) for index, character in enumerate(text)])


# Latin presentation-form ligatures as one glyph each: ff, fi, fl, ffi, ffl, long s t, st
write(
    "latin-ligatures.pdf",
    placed_doc([
        left_to_right(700, "e\uFB00ect \uFB01nal \uFB02ow"),
        left_to_right(650, "o\uFB03ce ba\uFB04e"),
        left_to_right(600, "\uFB05 \uFB06"),
        left_to_right(550, "plain office"),
    ]),
)

# Hebrew and Arabic written the way most producers write them: in visual order, so each right-to-left run's glyphs advance
# left to right across the page (the characters below are the logical text reversed, run by run, as drawn)
HEBREW_SHALOM = "\u05e9\u05dc\u05d5\u05dd"
HEBREW_OLAM = "\u05e2\u05d5\u05dc\u05dd"
HEBREW_MECHIR = "\u05de\u05d7\u05d9\u05e8"
HEBREW_SHEKEL = "\u05e9\u05e7\u05dc"
ARABIC_MARHABA = "\u0645\u0631\u062d\u0628\u0627"
ARABIC_BILALAM = "\u0628\u0627\u0644\u0639\u0627\u0644\u0645"
write(
    "rtl-visual-order.pdf",
    placed_doc([
        left_to_right(700, "." + HEBREW_OLAM[::-1] + " " + HEBREW_SHALOM[::-1]),  # a Hebrew sentence with its full stop
        left_to_right(650, "The word " + HEBREW_SHALOM[::-1] + " means peace."),  # a left-to-right sentence with one Hebrew word
        left_to_right(600, HEBREW_SHEKEL[::-1] + " 25.5 " + HEBREW_MECHIR[::-1]),  # a number inside Hebrew keeps its digit order
        left_to_right(550, HEBREW_OLAM[::-1] + " abc " + HEBREW_SHALOM[::-1]),  # a Latin run between two Hebrew words
        left_to_right(500, ARABIC_BILALAM[::-1] + " " + ARABIC_MARHABA[::-1]),
        left_to_right(450, "Plain ASCII line 123"),
    ]),
)

# The same kind of text written in logical order: each glyph is placed to the left of the one before it
write(
    "rtl-logical-order.pdf",
    placed_doc([
        right_to_left(700, HEBREW_SHALOM + " " + HEBREW_OLAM),
        right_to_left(650, ARABIC_MARHABA + " " + ARABIC_BILALAM),
    ]),
)

# ---- active content that must never run, be followed, or be emitted
js_action = b"<< /S /JavaScript /JS (app.alert\\('JS-ACTION-SENTINEL'\\);) >>"
objs = {
    1: b"<< /Type /Catalog /Pages 2 0 R /OpenAction 8 0 R /AcroForm 15 0 R /Names << /EmbeddedFiles 10 0 R /JavaScript 13 0 R >> >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"4 0 R", extra=b"/Annots [9 0 R 14 0 R 16 0 R] /AA << /O 8 0 R >>"),
    4: stream(b"", show(b"ACTIVE-CONTENT-VISIBLE-TEXT")),
    5: FONT,
    8: js_action,
    9: b"<< /Type /Annot /Subtype /Link /Rect [72 690 300 720] /A << /S /Launch /F (cmd.exe) /Win << /F (cmd.exe) /P (/c calc LAUNCH-ACTION-SENTINEL) >> >> >>",
    10: b"<< /Names [(attached.txt) 11 0 R] >>",
    11: b"<< /Type /Filespec /F (attached.txt) /EF << /F 12 0 R >> >>",
    12: stream(b"/Type /EmbeddedFile", b"EMBEDDED-FILE-SENTINEL-CONTENT"),
    13: b"<< /Names [(init) 8 0 R] >>",
    14: b"<< /Type /Annot /Subtype /Link /Rect [72 650 300 680] /A << /S /URI /URI (http://uri-sentinel.invalid/) >> >>",
    15: b"<< /Fields [16 0 R] /DA (/Helv 0 Tf 0 g) >>",
    16: b"<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /V (FORM-FIELD-SENTINEL-VALUE) /Rect [72 600 300 620] /P 3 0 R /AP << /N 17 0 R >> >>",
    17: stream(b"/Type /XObject /Subtype /Form /BBox [0 0 228 20] /Resources << /Font << /F1 5 0 R >> >>", b"BT /F1 12 Tf 2 4 Td (FORM-APPEARANCE-SENTINEL) Tj ET\n"),
}
write("active-content.pdf", build(objs))

# ---- structurally broken inputs
rng = random.Random(163)
write("empty.pdf", b"")
write("garbage.pdf", rng.randbytes(4096))
write("header-only.pdf", b"%PDF-1.4\n%%EOF\n")

cups_text = os.path.join(OUT, "cups-text.pdf")
if os.path.exists(cups_text):
    with open(cups_text, "rb") as f:
        t = f.read()
    write("truncated.pdf", t[: len(t) // 2])
else:
    print("cups-text.pdf not found next to the output: truncated.pdf skipped", file=sys.stderr)

write("bad-startxref.pdf", build({
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"4 0 R"),
    4: stream(b"", show(b"BROKEN-XREF-TEXT")),
    5: FONT,
}, startxref_override=11))
write("bad-xref-offsets.pdf", build({
    1: b"<< /Type /Catalog /Pages 2 0 R >>",
    2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    3: page_obj(b"4 0 R"),
    4: stream(b"", show(b"BROKEN-XREF-TEXT")),
    5: FONT,
}, xref_shift=7))

# ---- a PDF 1.5 file whose catalog, page tree, page and font live in a compressed object stream, found only through the
# cross-reference STREAM. With an intact stream it extracts; with the stream's entries garbled there is nothing for a
# repair scan to find (the objects are not written as "n 0 obj" at top level), so no reader can open it.
def xref_stream_doc(garble):
    font = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    members = [
        (1, b"<< /Type /Catalog /Pages 2 0 R >>"),
        (2, b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        (3, b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
        (5, font),
    ]
    index = b""
    body = b""
    for number, data in members:
        index += b"%d %d " % (number, len(body))
        body += data + b"\n"
    objstm = index + body
    out = bytearray(b"%PDF-1.5\n%\xe2\xe3\xcf\xd3\n")
    offsets = {}
    content = show(b"XREF-STREAM-TEXT")
    offsets[4] = len(out)
    out += b"4 0 obj\n" + stream(b"", content) + b"\nendobj\n"
    offsets[6] = len(out)
    out += b"6 0 obj\n<< /Type /ObjStm /N 4 /First %d /Length %d >>\nstream\n" % (len(index), len(objstm)) + objstm + b"\nendstream\nendobj\n"
    offsets[7] = len(out)
    # (type, field 2, field 3): type 2 is "compressed in object stream 6, at index field 3"
    entries = {
        0: (0, 0, 255),
        1: (2, 6, 0),
        2: (2, 6, 1),
        3: (2, 6, 2),
        4: (1, offsets[4], 0),
        5: (2, 6, 3),
        6: (1, offsets[6], 0),
        7: (1, offsets[7], 0),
    }
    data = b"".join(struct.pack(">BHB", *entries[number]) for number in range(8))
    if garble:
        data = bytes(byte ^ 0x5A for byte in data)
    out += b"7 0 obj\n<< /Type /XRef /Size 8 /W [1 2 1] /Root 1 0 R /Length %d >>\nstream\n" % len(data) + data + b"\nendstream\nendobj\n"
    out += b"startxref\n%d\n%%%%EOF\n" % offsets[7]
    return bytes(out)


write("xref-stream.pdf", xref_stream_doc(garble=False))
write("xref-stream-garbled.pdf", xref_stream_doc(garble=True))

# PdfPig repairs both of the files above by scanning for objects even with lenient parsing off, so they
# still extract. These two it cannot repair: a trailer it cannot read, and a trailer with no catalog.
multi = build(multi_objs)
assert b"/Size 11 /Root 1 0 R" in multi
write("bad-trailer.pdf", multi.replace(b"/Size 11 /Root 1 0 R", b"/Size abc /Root xyz"))
write("missing-root.pdf", multi.replace(b"/Root 1 0 R", b""))


# ---- worker-only: these cost seconds and gigabytes in-process. Never load them in an in-process test.
def flate_doc(content):
    z = zlib.compress(content, 9)
    return simple_doc(z, content_entries=b"/Filter /FlateDecode"), len(z)


bomb = b" " * 200_000_000 + b"\nBT /F1 18 Tf 72 700 Td (AFTER-BOMB-TEXT) Tj ET\n"
data, zlen = flate_doc(bomb)
write("flate-bomb.pdf", data)
print(f"  flate-bomb inflates to {len(bomb)} bytes from {zlen} compressed")

for label, n in [("1m", 1_000_000), ("5m", 5_000_000), ("20m", 20_000_000)]:
    content = b"BT /F1 6 Tf 72 700 Td (" + b"A" * n + b") Tj ET\n"
    data, zlen = flate_doc(content)
    write(f"flate-letters-{label}.pdf", data)
    print(f"  flate-letters-{label}: {n} glyphs from {zlen} compressed bytes")

# A form XObject chain 30 deep in which every form invokes the next TWICE (2^29 invocations of the last, which draws
# nothing). It is nested well inside the parser's 50-level limit, has no cycle, shows one line of visible text, and costs
# about 40 minutes of CPU and little memory in an unlimited parser, so only a wall clock or CPU limit ends it.
def fan_out_doc(depth, fan):
    objs = {
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: page_obj(b"4 0 R", resources_extra=b"/XObject << /Fa 6 0 R >>"),
        4: stream(b"", show(b"FAN-OUT-VISIBLE-TEXT") + b"/Fa Do\n"),
        5: FONT,
    }
    for index in range(depth):
        number = 6 + index
        last = index == depth - 1
        resources = b"/Resources << >>" if last else b"/Resources << /XObject << /Fa %d 0 R >> >>" % (number + 1)
        body = b"\n" if last else b"/Fa Do\n" * fan
        objs[number] = stream(b"/Type /XObject /Subtype /Form /BBox [0 0 612 792] " + resources, body)
    return build(objs)


write("xobject-fan-out.pdf", fan_out_doc(30, 2))

# ---- not committed: larger than the 1 MiB attachment intake ceiling, so the worker would refuse them at stdin
if LARGE:
    depth = 200000
    nest = b"<< /A " * depth + b"1" + b" >>" * depth
    write("deep-dict-info.pdf", simple_doc(show(b"DEEP-DICT-VISIBLE"), extra_objs={6: nest}, trailer_extra=b"/Info 6 0 R"))
    write("deep-dict-resources.pdf", simple_doc(show(b"DEEP-DICT-VISIBLE"), extra_objs={6: nest}, page_extra=b"/Group 6 0 R /Annots [6 0 R]"))
    write("deep-pages-100k.pdf", deep_pages(100000))

    def deep_xobject(depth):
        objs = {
            1: b"<< /Type /Catalog /Pages 2 0 R >>",
            2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            3: page_obj(b"4 0 R", resources_extra=b"/XObject << /Fm1 6 0 R >>"),
            4: stream(b"", show(b"BEFORE-CHAIN") + b"/Fm1 Do\n"),
            5: FONT,
        }
        for i in range(depth):
            n = 6 + i
            nxt = n + 1 if i < depth - 1 else None
            res = b"/Resources << /Font << /F1 5 0 R >> %s >>" % (b"/XObject << /Fm1 %d 0 R >>" % nxt if nxt else b"")
            body = (b"/Fm1 Do\n" if nxt else b"") + b"BT /F1 12 Tf 72 %d Td (FORM-%d) Tj ET\n" % (600 - (i % 400), i)
            objs[n] = stream(b"/Type /XObject /Subtype /Form /BBox [0 0 612 792] " + res, body)
        return build(objs)

    write("deep-xobject-chain-5k.pdf", deep_xobject(5000))
    write("deep-xobject-chain-50k.pdf", deep_xobject(50000))
    write("q-nesting-1m.pdf", simple_doc(b"q " * 1_000_000 + show(b"INSIDE-Q-NEST")))
