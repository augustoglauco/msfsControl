# Reference server (test only)

Implements the wire protocol documented in ../../../docs/video/SPEC.md: UDP discovery reply, identification, preamble, H.264 frames with ACK pacing, touch events.
Video is an uncompressed I_PCM H.264 test pattern (no encoder needed, very large frames: use on a LAN).

    python3 server.py --port 28352          # default port avoids the real service on 28252
    python3 test_client.py 28352            # loopback self-test (framing + NAL structure)

Verified: framing and NAL structure against the loopback test client only.
NOT verified: that a real client decodes the stream or accepts this handshake, ACK window (--window, default 2), behaviour on a non-default port.
Notes: the real client may only try port 28252 (stop the vendor service or use another host and pass --port 28252). From WSL2 a phone cannot reach the server without port forwarding; run it with a Windows Python instead.
