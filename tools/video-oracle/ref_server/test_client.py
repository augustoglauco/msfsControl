"""Loopback client: discovery, identification, receives frames, ACKs, sends touch; checks framing and NAL structure."""
import socket, struct, sys, time, re
import proto
port = int(sys.argv[1]) if len(sys.argv) > 1 else 28352
u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); u.settimeout(2)
u.sendto(proto.DISCOVERY_REQUEST, ("127.0.0.1", port)); r, _ = u.recvfrom(2048)
name = r[:260].decode("utf-16le").rstrip("\x00"); tcp = struct.unpack_from("<I", r, 260)[0]
print("discovery: len", len(r), "host", name, "tcp port", tcp); assert len(r) == 308
s = socket.create_connection(("127.0.0.1", tcp)); W, H = 640, 368
s.sendall(proto.identification(W, H, 30, "{00000000-0000-0000-0000-000000000000}", "TESTCLIENT"))
types = []; frames = 0; t0 = time.time()
while frames < 6:
    h, body = proto.read_message(s); types.append(h[0])
    if h[0] == proto.T_FRAME:
        assert (h[2], h[3], h[4], h[1], h[17]) == (W, H, W * 4, len(body), len(body)), list(h)
        nals = [x[0] & 31 for x in re.findall(rb"\x00\x00\x01(.)", body)]
        if frames == 0: print("first frame", len(body), "bytes, NAL types", nals)
        assert nals == [9, 7, 8, 5], nals
        s.sendall(proto.ack()); frames += 1
        if frames == 2: s.sendall(proto.touch(100, 50, W, H, 3, 1000)); s.sendall(proto.touch(110, 60, W, H, 5, 1016))
print("preamble types", types[:5], "frames", frames, "in %.1fs" % (time.time() - t0)); print("OK")
