"""Reference server for the protocol in ../../../docs/video/SPEC.md. Test pattern video via I_PCM H.264. Not the vendor's software."""
import argparse, socket, ssl, struct, threading, time, itertools
import proto, h264_pcm

def pattern(w, tick):
    def rows(y):
        mbw = (w + 15) // 16 * 16
        bar = (tick * 8) % mbw
        base = bytes(max(1, min(255, 40 + (x * 180) // mbw)) for x in range(mbw))
        return bytes(255 if bar <= x < bar + 24 else v for x, v in enumerate(base))
    return rows

def upright(mx, my, mbw, mbh, r):
    # Windows keeps the frame in the monitor's native orientation and rotates the desktop inside it (DISPLAYCONFIG_ROTATION 1..4).
    # Returns the desktop ("upright") macroblock shown at native frame position (mx, my), plus the upright grid size.
    if r == 2: return mbh - 1 - my, mx, mbh, mbw
    if r == 3: return mbw - 1 - mx, mbh - 1 - my, mbw, mbh
    if r == 4: return my, mbw - 1 - mx, mbh, mbw
    return mx, my, mbw, mbh

def frame_data(video, w, hh, tick, r=1):
    if video == "gray": return h264_pcm.idr_gray(w, hh, tick)
    if video == "pcm": return h264_pcm.idr_frame(w, hh, pattern(w, tick), tick)
    mbw = (w + 15) // 16; mbh = (hh + 15) // 16
    def target(mx, my):
        ux, uy, uw, uh = upright(mx, my, mbw, mbh, r)
        if ux < 4 and uy < 4: return 255                      # white square = top-left of the desktop
        bar = (tick * 2) % uw                                  # bar runs left to right on the desktop
        return 255 if bar <= ux < bar + 3 else 40 + (ux * 150) // uw + (uy % 2) * 20
    return h264_pcm.idr_pattern(w, hh, target, tick)

def discovery(host, tcp_port, udp_port, name, tls=False):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((host, udp_port)); reply = proto.discovery_reply(name, tcp_port, tls)
    while True:
        data, addr = s.recvfrom(1024)
        if data.startswith(proto.DISCOVERY_REQUEST[:-1]):
            s.sendto(reply, addr)

# The vendor service appends this constant to the registry EncryptionPassword before hex-decoding it into the PSK (fn 0x14008ca50) [obs].
PSK_SUFFIX = "1A" * 16

def psk_context(key):
    # The vendor service speaks TLS 1.3 with a pre-shared key (hex password, identity "Client_identity"), no certificate needed.
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.minimum_version = ssl.TLSVersion.TLSv1_3
    ctx.set_psk_server_callback(lambda identity: key)
    return ctx

def session(conn, addr, window, video, log, psk=None, follow=True):
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    if psk and conn.recv(1, socket.MSG_PEEK) != b"\x16":
        log(f"{addr}: client sent plain TCP (no TLS), serving plain"); psk = None
    if psk:
        try: conn = psk.wrap_socket(conn, server_side=True)
        except (ssl.SSLError, OSError) as e: log(f"{addr}: tls handshake failed: {e}"); return
        log(f"{addr}: tls {conn.version()} {conn.cipher()[0]}")
    h, body = proto.read_message(conn)
    if h[0] != proto.T_IDENT or h[1] != 334:
        log(f"{addr}: bad identification type={h[0]} count={h[1]}"); return
    w, hh, fps = h[13] or 1280, h[22] or 720, h[8] or 30
    # Rotation (type 9, int 2) is a DISPLAYCONFIG_ROTATION (1 identity, 2 = 90, 3 = 180, 4 = 270). The vendor service rotates the
    # Windows display with it; the frame size never changes, only the desktop content inside the frame (SPEC 15.10).
    rot = {"r": 1}
    log(f"{addr}: ident {w}x{hh}@{fps} dev={body[:78].decode('utf-16le').rstrip(chr(0))!r}")
    for m in (proto.pack_header(i0=proto.T_SETTINGS), proto.pack_header(i0=proto.T_VISIBILITY, i3=1),
              proto.pack_header(i0=proto.T_VISIBILITY, i2=1, i3=1), proto.pack_header(i0=proto.T_PING),
              proto.pack_header(i0=proto.T_PING)):
        conn.sendall(m)
    credit = threading.Semaphore(window); alive = threading.Event(); alive.set()
    stats = {"sent": 0, "acks": 0, "bytes": 0}

    def reader():
        try:
            while True:
                mh, mb = proto.read_message(conn)
                if mh[0] == proto.T_ACK: stats["acks"] += 1; credit.release()
                elif mh[0] == proto.T_TOUCH:
                    if mh[6] != 1: log(f"touch x={mh[2]} y={mh[3]} phase={mh[6]} ts={mh[7]}")
                elif mh[0] == proto.T_ROTATION:
                    if follow and 1 <= mh[2] <= 4: rot["r"] = mh[2]
                    log(f"rotation {mh[2]} ({'applied to content, frame stays ' + str(w) + 'x' + str(hh) if follow else 'ignored'}) sent={stats['sent']} acks={stats['acks']}")
                else: log(f"client msg type={mh[0]} ints={[(i, v) for i, v in enumerate(mh) if v]} body={mb.hex()}")
        except Exception: pass
        alive.clear(); [credit.release() for _ in range(window)]
    threading.Thread(target=reader, daemon=True).start()
    period = 1.0 / fps; nxt = time.time(); last = time.time()
    for tick in itertools.count():
        if not alive.is_set(): break
        credit.acquire()
        data = frame_data(video, w, hh, tick, rot["r"])
        try: conn.sendall(proto.frame_header(len(data), w, hh, fps) + data)
        except OSError: break
        stats["sent"] += 1; stats["bytes"] += len(data)
        if time.time() - last > 2: log(f"{addr}: sent={stats['sent']} acks={stats['acks']} bytes={stats['bytes']}"); last = time.time()
        nxt += period; time.sleep(max(0, nxt - time.time()))
    log(f"{addr}: closed")

def main():
    a = argparse.ArgumentParser()
    a.add_argument("--host", default="0.0.0.0"); a.add_argument("--port", type=int, default=28352)
    a.add_argument("--name", default="refserver"); a.add_argument("--window", type=int, default=2); a.add_argument("--logfile"); a.add_argument("--no-follow-rotation", action="store_true"); a.add_argument("--psk", help="EncryptionPassword (8 hex digits, as in the registry) to serve TLS 1.3 PSK instead of plain TCP"); a.add_argument("--video", choices=["pattern", "gray", "pcm"], default="pattern")
    a = a.parse_args()
    lf = open(a.logfile, "a") if a.logfile else None
    def log(s):
        line = f"{time.strftime('%H:%M:%S')} {s}"; print(line, flush=True)
        if lf: lf.write(line + "\n"); lf.flush()
    psk = psk_context(bytes.fromhex(a.psk + PSK_SUFFIX)) if a.psk else None
    threading.Thread(target=discovery, args=(a.host, a.port, a.port, a.name, bool(psk)), daemon=True).start()
    ls = socket.socket(); ls.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); ls.bind((a.host, a.port)); ls.listen(4)
    log(f"listening tcp+udp {a.host}:{a.port}")
    while True:
        c, addr = ls.accept()
        threading.Thread(target=session, args=(c, addr, a.window, a.video, log, psk, not a.no_follow_rotation), daemon=True).start()

if __name__ == "__main__":
    main()
