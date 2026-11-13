"""Wire constants and helpers (see ../../../docs/video/SPEC.md sections 7, 12, 13). Values marked [unk] in the spec are copied from captures."""
import struct

HEADER_SIZE = 128
T_IDENT, T_PING, T_FRAME, T_VISIBILITY, T_SETTINGS = 0, 1, 2, 3, 4
T_ACK, T_ERROR, T_MOUSE, T_KEYBOARD, T_TOUCH, T_PEN = 7, 8, 10, 11, 12, 13
T_ROTATION = 9
DISCOVERY_REQUEST = b"SPACEDESK-NET-CLIENT\x00"
DISCOVERY_REPLY_SIZE = 308


def pack_header(**ints):
    h = [0] * 32
    for k, v in ints.items():
        h[int(k[1:])] = v          # keys look like i0=..., i1=...
    return struct.pack("<32I", *h)


def read_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("peer closed")
        buf += chunk
    return bytes(buf)


def read_message(sock):
    h = struct.unpack("<32I", read_exact(sock, HEADER_SIZE))
    return h, read_exact(sock, h[1]) if h[1] else b""


def frame_header(size, w, h, fps):
    return pack_header(i0=T_FRAME, i1=size, i2=w, i3=h, i4=w * 4, i5=21,
                       i8=w, i9=h, i10=4, i11=2, i12=fps, i13=4, i15=1, i17=size)


def discovery_reply(hostname, tcp_port, tls=False):
    b = bytearray(DISCOVERY_REPLY_SIZE)
    name = hostname.encode("utf-16le")[:258]
    b[0:len(name)] = name
    struct.pack_into("<I", b, 260, tcp_port)
    struct.pack_into("<I", b, 264, 10)      # [unk]
    struct.pack_into("<III", b, 272, 4, 8, 43 | (0x10 if tls else 0))  # [unk]; 0x10 seen when the vendor service has EncryptionSslEnable=1 [inf]
    return bytes(b)


def identification(width, height, fps, device_id, device_name):
    body = bytearray(334)
    s = (device_id + "\x00" + device_name).encode("utf-16le")
    body[0:len(s)] = s
    h = pack_header(i0=T_IDENT, i1=334, i2=4, i3=8, i5=3, i6=3, i7=2, i8=fps,
                    i9=0x00010005, i11=0x0004003C, i12=1, i13=width, i22=height, i31=1)
    return h + bytes(body)


def touch(x, y, w, h, phase, ts_ms):
    return pack_header(i0=T_TOUCH, i2=x, i3=y, i4=w, i5=h, i6=phase, i7=ts_ms & 0xFFFFFFFF)


def ack():
    return pack_header(i0=T_ACK)
