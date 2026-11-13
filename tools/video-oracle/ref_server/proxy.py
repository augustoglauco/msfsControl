"""Logging proxy: client <-> this proxy (TLS 1.3 PSK) <-> vendor service (TLS 1.3 PSK). Shows the vendor's decrypted traffic. Not the vendor's software."""
import argparse, socket, ssl, struct, threading, time
import proto, server

def summarize(h):
    return [(i, v) for i, v in enumerate(h) if v]

def pump(src, dst, tag, log, stats):
    last_frame = None; last = time.time()
    try:
        while True:
            h, body = proto.read_message(src)
            raw = struct.pack("<32I", *h) + body
            t = h[0]
            if t == proto.T_FRAME:
                key = tuple(v for i, v in enumerate(h) if i not in (1, 17))
                if key != last_frame:
                    log(f"{tag} frame header changed: {summarize(h)} body={len(body)}"); last_frame = key
                stats["frames"] += 1
            elif t == proto.T_ACK: stats["acks"] += 1
            elif t in (proto.T_MOUSE, proto.T_TOUCH): stats["input"] += 1
            else: log(f"{tag} type={t} ints={summarize(h)} body={len(body)}" + (f" hex={body[:48].hex()}" if body else ""))
            with stats["lock"]: dst.sendall(raw)
            if time.time() - last > 2:
                log(f"  stats frames={stats['frames']} acks={stats['acks']} input={stats['input']}"); last = time.time()
    except Exception as e:
        log(f"{tag} closed ({type(e).__name__})")
    for s in (src, dst):
        try: s.shutdown(socket.SHUT_RDWR)
        except OSError: pass

def handle(conn, addr, up_addr, psk, key, log):
    try:
        conn = psk.wrap_socket(conn, server_side=True)
    except (ssl.SSLError, OSError) as e:
        log(f"{addr}: client tls handshake failed: {e}"); return
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
    ctx.minimum_version = ssl.TLSVersion.TLSv1_3; ctx.set_psk_client_callback(lambda hint: ("Client_identity", key))
    try:
        up = ctx.wrap_socket(socket.create_connection(up_addr, timeout=5)); up.settimeout(None)
    except (ssl.SSLError, OSError) as e:
        log(f"{addr}: upstream connect failed: {e}"); return
    log(f"{addr}: connected through proxy, client {conn.cipher()[0]}, upstream {up.cipher()[0]}")
    # Each direction keeps its own lock so concurrent forwards never interleave records on one socket.
    sa = {"frames": 0, "acks": 0, "input": 0, "lock": threading.Lock()}; sb = {"frames": 0, "acks": 0, "input": 0, "lock": threading.Lock()}
    threading.Thread(target=pump, args=(up, conn, "SERVER->CLIENT", log, sa), daemon=True).start()
    pump(conn, up, "CLIENT->SERVER", log, sb)

def main():
    a = argparse.ArgumentParser()
    a.add_argument("--listen-port", type=int, default=28253); a.add_argument("--upstream", default="127.0.0.1:28252")
    a.add_argument("--discovery-port", type=int, default=28252); a.add_argument("--name", default="PROXY"); a.add_argument("--psk", required=True)
    a.add_argument("--logfile"); a = a.parse_args()
    lf = open(a.logfile, "a") if a.logfile else None
    def log(s):
        line = f"{time.strftime('%H:%M:%S')} {s}"; print(line, flush=True)
        if lf: lf.write(line + "\n"); lf.flush()
    key = bytes.fromhex(a.psk + server.PSK_SUFFIX); psk = server.psk_context(key)
    host, port = a.upstream.rsplit(":", 1)
    def disc():
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try: s.bind(("0.0.0.0", a.discovery_port))
        except OSError as e: log(f"discovery bind failed ({e}); the phone must find the proxy another way"); return
        reply = proto.discovery_reply(a.name, a.listen_port, True); log(f"discovery answering on udp {a.discovery_port} with tcp port {a.listen_port}")
        while True:
            data, addr = s.recvfrom(1024)
            if data.startswith(proto.DISCOVERY_REQUEST[:-1]): s.sendto(reply, addr); log(f"discovery request from {addr}")
    threading.Thread(target=disc, daemon=True).start()
    ls = socket.socket(); ls.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); ls.bind(("0.0.0.0", a.listen_port)); ls.listen(4)
    log(f"proxy listening tcp {a.listen_port} -> {a.upstream}")
    while True:
        c, addr = ls.accept(); c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        threading.Thread(target=handle, args=(c, addr, (host, int(port)), psk, key, log), daemon=True).start()

if __name__ == "__main__":
    main()
