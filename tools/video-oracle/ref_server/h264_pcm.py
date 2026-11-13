"""Minimal valid H.264 Annex B writer using only I_PCM macroblocks (baseline, CAVLC). Uncompressed: test use only."""
import re

class Bits:
    def __init__(self): self.v = 0; self.n = 0
    def u(self, n, x): self.v = (self.v << n) | (x & ((1 << n) - 1)); self.n += n
    def ue(self, x):
        x += 1; l = x.bit_length(); self.u(l - 1, 0); self.u(l, x)
    def se(self, x): self.ue(2 * x - 1 if x > 0 else -2 * x)
    def align0(self):
        while self.n % 8: self.u(1, 0)
    def trail(self): self.u(1, 1); self.align0()
    def raw(self, b): self.align0(); self.v = (self.v << (8 * len(b))) | int.from_bytes(b, "big"); self.n += 8 * len(b)
    def bytes(self): return self.v.to_bytes(self.n // 8, "big")

def _nal(ref_idc, typ, rbsp):
    esc = re.sub(rb"\x00\x00(?=[\x00-\x03])", b"\x00\x00\x03", rbsp)
    return b"\x00\x00\x00\x01" + bytes([(ref_idc << 5) | typ]) + esc

def sps(w, h, level=42, profile=66, flags=0xC0):
    mbw, mbh = (w + 15) // 16, (h + 15) // 16
    b = Bits(); b.u(8, profile); b.u(8, flags); b.u(8, level); b.ue(0); b.ue(0); b.ue(2); b.ue(1); b.u(1, 0)
    b.ue(mbw - 1); b.ue(mbh - 1); b.u(1, 1); b.u(1, 1)
    cr, cb = (mbw * 16 - w) // 2, (mbh * 16 - h) // 2
    if cr or cb: b.u(1, 1); b.ue(0); b.ue(cr); b.ue(0); b.ue(cb)
    else: b.u(1, 0)
    b.u(1, 0); b.trail(); return _nal(3, 7, b.bytes())

def pps(deblock_ctrl=False):
    b = Bits(); b.ue(0); b.ue(0); b.u(1, 0); b.u(1, 0); b.ue(0); b.ue(0); b.ue(0); b.u(1, 0); b.u(2, 0)
    b.se(0); b.se(0); b.se(0); b.u(1, 1 if deblock_ctrl else 0); b.u(1, 0); b.u(1, 0); b.trail(); return _nal(3, 8, b.bytes())

def idr_frame(w, h, luma_rows, idr_id=0):
    """luma_rows(y) -> bytes of length mbw*16 for luma row y (values 1..255)."""
    mbw, mbh = (w + 15) // 16, (h + 15) // 16
    b = Bits(); b.ue(0); b.ue(7); b.ue(0); b.u(4, 0); b.ue(idr_id & 0xFF); b.u(1, 0); b.u(1, 0); b.se(0)
    chroma = bytes([128]) * 64
    rows = [luma_rows(y) for y in range(mbh * 16)]
    for my in range(mbh):
        for mx in range(mbw):
            b.ue(25); b.align0()
            blk = b"".join(rows[my * 16 + r][mx * 16:mx * 16 + 16] for r in range(16))
            b.raw(blk + chroma + chroma)
    b.trail()
    aud = b"\x00\x00\x00\x01\x09\x10"
    return aud + sps(w, h) + pps() + _nal(3, 5, b.bytes())


def idr_gray(w, h, idr_id=0, profile=77, flags=0x40):
    """Tiny valid IDR: every MB is I16x16 DC-pred, no residual -> flat mid-gray. ~1 byte per macroblock."""
    mbw, mbh = (w + 15) // 16, (h + 15) // 16
    b = Bits(); b.ue(0); b.ue(7); b.ue(0); b.u(4, 0); b.ue(idr_id & 0xFF); b.u(1, 0); b.u(1, 0); b.se(0)
    for _ in range(mbw * mbh):
        b.ue(3); b.ue(0); b.se(0); b.u(1, 1)   # mb_type I_16x16_2_0_0, chroma pred DC, qp_delta 0, DC coeff_token TotalCoeff=0
    b.trail()
    aud = b"\x00\x00\x00\x01\x09\x10"
    return aud + sps(w, h, 42, profile, flags) + pps() + _nal(3, 5, b.bytes())


def _dc_coeff(b, c):
    """Intra16x16DCLevel block holding a single DC level c at scan position 0 (CAVLC, nC 0..1)."""
    if c == 0: b.u(1, 1); return
    if abs(c) == 1:
        b.u(2, 1); b.u(1, 1 if c < 0 else 0); b.u(1, 1); return    # coeff_token(T1=1,TC=1), sign, total_zeros=0
    b.u(6, 5)                                                       # coeff_token(T1=0,TC=1)
    code = (2 * abs(c) - 2 if c > 0 else 2 * abs(c) - 1) - 2        # levelCode - 2 (T1 < 3), suffixLength 0
    assert 0 <= code < 14
    b.u(code + 1, 1); b.u(1, 1)                                     # level_prefix = code, total_zeros=0

QP, STEP = 51, 14   # slice QP 51 -> one DC level moves a flat MB's luma by 14 (see notes in SPEC)

def idr_pattern(w, h, target, idr_id=0, profile=77, flags=0x40):
    """IDR of flat luma macroblocks: target(mx, my) -> luma 0..255. Chroma stays 128. DC-prediction is simulated."""
    mbw, mbh = (w + 15) // 16, (h + 15) // 16
    b = Bits(); b.ue(0); b.ue(7); b.ue(0); b.u(4, 0); b.ue(idr_id & 0xFF); b.u(1, 0); b.u(1, 0)
    b.se(QP - 26); b.ue(1)                                          # slice_qp_delta, disable_deblocking_filter_idc=1
    rec = [[128] * mbw for _ in range(mbh)]
    for my in range(mbh):
        for mx in range(mbw):
            if mx and my: pred = (rec[my][mx - 1] + rec[my - 1][mx] + 1) >> 1
            elif my: pred = rec[my - 1][mx]
            elif mx: pred = rec[my][mx - 1]
            else: pred = 128
            c = max(-8, min(8, round((target(mx, my) - pred) / STEP)))
            rec[my][mx] = max(0, min(255, pred + STEP * c))
            b.ue(3); b.ue(0); b.se(0); _dc_coeff(b, c)
    b.trail()
    aud = b"\x00\x00\x00\x01\x09\x10"
    return aud + sps(w, h, 42, profile, flags) + pps(True) + _nal(3, 5, b.bytes())
