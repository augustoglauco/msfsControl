# Remote display streaming: design notes and clean-room plan

Source of the notes: static analysis of `spacedeskService.exe` (SHA-256 d2a7f15f…1508) with REA/Ghidra 12.2.
Labels: **[obs]** observed in imports/strings/decompiled code, **[inf]** inferred, **[unk]** not yet analysed.
Nothing here was run. Write your own code from this design and public Microsoft APIs; do not paste decompiler output.

## 1. Pipeline (server side)

```
Desktop Duplication (DXGI)  ->  per-display pipeline  ->  task scheduler (thread pool)  ->  encoder  ->  transmitter  ->  socket
```

| Stage                                                                                                                                                    | Evidence                                                                    |
| -------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- |
| Capture: `IDXGIOutput1::DuplicateOutput`, `AcquireNextFrame(1000 ms)`, QI to `ID3D11Texture2D`, `GetDesc`, shader-resource view over the texture | [obs] fn 0x1400c9230; imports `CreateDXGIFactory1`, `D3D11CreateDevice` |
| `DXGI_ERROR_WAIT_TIMEOUT` (0x887A0027) on acquire is the normal "no change" case and is not logged as an error                                         | [obs] fn 0x1400c9230                                                        |
| One `SEDisplayPipeline` per display; a frame-processor thread loops PROCESS / ADJUST / RESET                                                           | [obs] strings `SEThreadFrameProcessorDisplay::Run ...`                    |
| `SubmitFrameRegular` vs `TriggerFrameHighQuality`                                                                                                    | [obs] strings; semantics [inf] (normal frame vs quality refresh)            |
| Encode jobs are `SETask*` objects run on `SETaskSchedulerCodec` threads                                                                              | [obs] fn 0x1400c4be0 allocates them and submits to the scheduler            |
| `Stage01TargetsAcquire` -> ... -> `Stage03EncodingStart` -> transmit                                                                                 | [obs] names; stage 02 not analysed                                          |

## 2. Encoder selection (fn 0x1400c4be0, `L6Codec`-style switch on a mode field)

Mode field at pipeline+0xC0 [obs]:

| Mode | Task                               | Notes                                                |
| ---- | ---------------------------------- | ---------------------------------------------------- |
| 1    | RGB (raw)                          | [inf] from remaining error string `StartEncodeRgb` |
| 2    | `SETaskEncodeYuvGpu`             | GPU RGB->YUV conversion                              |
| 3    | `SETaskEncodeYuvCpu`             | CPU conversion on a sub-rect                         |
| 4    | JPEG CPU (`SETaskEncodeJpegCpu`) | [inf] mapping; libjpeg-turbo statically linked       |
| 5    | `SETaskEncodeMftH264`            | Media Foundation H.264 MFT                           |

Each task carries: source buffer, **sub-rectangle** (x, y, w, h, clamped by a helper to the frame size), frame width/height, a **quality** value, an output slot, and a back-pointer to the codec context [obs].
This means encoding is **per region**, not necessarily whole-frame [inf: likely driven by dirty rects].

## 3. H.264 task (fn 0x1400c74a0) [obs]

Calls virtual `EncodeH264(buffer, width, height, stride = width*4, quality, &rect, highQualityFlag)`.

- Input is 32-bit packed pixels (BGRA from DXGI), stride = width * 4.
- `quality` is 0-100, default 70 (0x46) in the task object; a high-quality refresh frame forces 100, otherwise it uses the pipeline's configured value (pipeline+0xB8).
- Config keys seen in strings: `H264GopSize`, `H264KeyFrameForce`, `H264SwEncodingForce` (use the software MFT instead of the GPU one), `JpegHuffOptimized`.
- Media Foundation imports (delay-loaded): `MFStartup`, `MFTEnumEx`, `MFCreateDXGIDeviceManager`, `MFCreateDXGISurfaceBuffer`, `MFCreateSample`, `MFCreateMemoryBuffer`, `MFCreateMediaType`.

## 4. Transport and control (superseded in part by section 7)

Observed only by name: overlapped Winsock (`WSASend/WSARecv/WSAAccept`), QoS flows (`qwave`), UDP/TCP discovery thread (`L4ThreadNetDiscovery`, port printed with `Port:%d`), OpenSSL context (`L4OpenSSLContext`), USB AOAP link (`L4DeviceUsbAoap`), a separate audio link.
**Not analysed:** packet framing, handshake, discovery payload, port numbers, input-event channel, congestion/QoS policy.
These are the next analysis targets if you need wire compatibility. If you only need *a* remote-video app, skip them and design your own protocol (section 6).

## 5. Reference build for a new application (public APIs only)

1. Capture: `D3D11CreateDevice` -> `IDXGIOutput1::DuplicateOutput` -> loop `AcquireNextFrame`; use `GetFrameDirtyRects` / `GetFrameMoveRects` for regions.
2. Convert/encode: Media Foundation H.264 encoder MFT (`MFT_CATEGORY_VIDEO_ENCODER`, hardware first, software fallback), NV12 input via `ID3D11VideoProcessor` (GPU) or CPU conversion; low-latency settings (`CODECAPI_AVLowLatencyMode`, `CODECAPI_AVEncCommonRateControlMode`, short GOP, on-demand IDR).
3. Pipeline: capture thread -> bounded queue -> encoder pool -> sender thread; drop old frames instead of queueing when the network is slow.
4. Transport: TCP (or UDP + your own loss recovery) with a small header (type, display id, frame id, rect, size, keyframe flag); TLS via Schannel/OpenSSL.
5. Client: decode with Media Foundation / FFmpeg / platform decoder, request an IDR on loss.
6. Optional: indirect display driver (IddCx) for a *virtual* monitor; the service here manages its own driver through SetupAPI/`SwDeviceCreate`.

## 6. What to analyse next for wire-level fidelity

- Transmitter thread (`m_ThreadTransmitter`) and packet builder.
- Stage 02 of `SEDisplayPipeline` and the dirty-rect logic.
- `L4ThreadNetDiscovery` payload and `L4OpenSSLContext` setup.
- Session/connection handshake and the input-event path.

## 7. Protocol findings (second analysis pass)

Labels as above. Function addresses are image-relative VAs in `spacedeskService.exe`.

### 7.1 Message types [obs] (fn 0x1400ec4c0, a header dump routine)

`PROTOCOL_SPCDSK_SMPL_HEADER_TYPE_*`, value = first int of the header:

| Value | Name                                               | Direction [inf]                        |
| ----- | -------------------------------------------------- | -------------------------------------- |
| 0     | IDENTIFICATION                                     | both (handshake)                       |
| 1     | PING (has an `Option` int at header int index 3) | server -> client on idle               |
| 2     | DISPLAY_FRAMEBUFFER                                | server -> client                       |
| 3     | DISPLAY_VISIBILITY                                 | server -> client                       |
| 4     | DISPLAY_SETTINGS                                   | server -> client                       |
| 5     | UNUSED_01                                          | -                                      |
| 6     | EVT_DISPLAY_SETTINGS                               | client -> server                       |
| 7     | FRAMEBUFFER_FLOW_CONTROL_ACK                       | client -> server (frame pacing)        |
| 8     | ERROR (carries a `Reason` int)                   | server -> client                       |
| 9     | ROTATION                                           | client -> server [inf]                 |
| 10    | EVT_INPUT_MOUSE                                    | client -> server                       |
| 11    | EVT_INPUT_KEYBOARD                                 | client -> server                       |
| 12    | EVT_INPUT_TOUCH                                    | client -> server                       |
| 13    | EVT_INPUT_PEN                                      | client -> server                       |
| 14    | AUDIO_SPEAKER                                      | server -> client (separate audio link) |

### 7.2 Header and handshake

- Header is at least 16 bytes of little-endian ints [inf from field offsets]; int 0 = type, int 1 = count of bytes following the header (name from a validation string: `CountbyteDataFollowingHeader`), int 3 = type-specific option (PING). Ints 2 and the remaining layout: [unk].
- Identification: type 0 header with 0x14E (334) following bytes is the only accepted shape; a header with 0 following bytes is rejected (fn 0x1400ef620) [obs]. The 334-byte body layout: [unk].
- The client sends identification first; the server receives it with two synchronous reads (header, then body), validates it, and only then proceeds (fn 0x1400e4460) [obs].
- QoS: TCP_NODELAY is set and SO_SNDBUF is read on attach [obs]. A WebSocket handshake path exists (`Sec-WebSocket-Key`, `101 Switching Protocols`, fn 0x1400e148c) [obs], presumably for browser clients; framing after upgrade: [unk].

### 7.3 Transports [obs]

- TCP regular and TCP+SSL (`L4SocketTcpCommRegular/SSL`) with a listener thread (`L4ThreadConnectionListener`). Listening port number: [unk].
- UDP discovery (fn 0x1400dfc80): the server binds a UDP socket, loops on `recv` (1024-byte buffer), and if the datagram **starts with a configured prefix string** it `sendto`s a configured reply buffer to the sender. The socket is recreated after failure with a 3 s sleep. Prefix string, reply payload and port come from config fields set in `Start`: [unk, not yet read]. The adapter list is rebuilt on IP interface changes (`NotifyIpInterfaceChange`).
- iOS over USB: the service connects through Apple's usbmuxd (fn 0x1400e4270) sending the standard 16-byte usbmuxd header (length, version 1, request 8, tag 1) plus a plist `MessageType=Connect`, `PortNumber` = "10505" (the plist field is the usbmuxd big-endian port encoding, so the device-side port is likely the byte-swapped value of 10505) [obs, port semantics inf]. Success is a usbmuxd Result message with number 0.
- Android over USB: AOAP bulk pipes (`L4DeviceUsbAoap`) [obs].
- A separate communication link carries audio [obs].

### 7.4 Server transmit path [obs]

- `L5ThreadTransmitter` (fn 0x1400eee30), priority TIME_CRITICAL: waits on a notification queue. Notification 1 = send queued buffers, 2 = set the ping interval, wait timeout = idle, so it builds and sends a PING. This is the keepalive.
- `SEThreadTransmitterDisplaySection` (fn 0x1400c6820), also TIME_CRITICAL: pulls encoded buffers from the display's queue and submits them as `DisplayTransmitFrame` items; a null encoded buffer becomes an error notification (`DisplayTransmitError`).
- Per-display section parameters logged on failure (fn 0x1400c6820) are the real tunables of the stream: **Width, Height, OffsetX, OffsetY, Rotation, Quality, Subsampling, EncodingType, FragmentCount, UpdateRects, Brightness, Contrast, MirrorFlip**. `EncodingType` 5 = H.264 (same enum as section 2). `FragmentCount` implies a frame can be split into fragments; `UpdateRects` implies partial-region updates. Their wire encoding: [unk].

### 7.5 Input [obs, partly inf]

- Client events EVT_INPUT_MOUSE/KEYBOARD/TOUCH/PEN are consumed by `SEDeviceInputMouse/Keyboard/Touch` and written to **virtual HID devices** through the service's own bus driver (`DeviceIoControl`, `IoctlMouseInputSend`, a `DeviceMousePlug`/`Unplug` API keyed by hardware ID) [obs].
- Mouse can be written as absolute touch or relative touch; client coordinates are validated and mapped to the desktop taking display **rotation** into account (`MouseCoordinatesMapToDesktop`) [obs].
- Keyboard events carry a scan code; invalid scan codes are rejected [obs]. Touch events are validated against the client resolution [obs].
- Wire layout of each input event: [unk].

## 8. Status: not yet a complete wire spec

Known well enough to design against: message set, handshake order, keepalive, per-display parameters, input pipeline, discovery behaviour, transports.
Still unknown and **blocking wire compatibility**: exact header layout (ints 2 and beyond), the 334-byte identification body, DISPLAY_SETTINGS / FRAMEBUFFER / FLOW_CONTROL_ACK payloads, fragment and update-rect encoding, input event payloads, discovery prefix/reply/port, TCP listening port, SSL negotiation details.
Next functions to decompile: the type-specific buffer builders (`L5L6BufferDisplay`, `CreatePing`), `L4ThreadNetDiscovery::Start`, the connection-listener `Run` (fn near 0x1400deb5e), and the input event parsers.
Verification gap: nothing was captured from a live client/server pair, so every layout above must be checked against a packet capture before trusting it.

## 9. Third pass: ping, transmit, listener [obs]

- **PING builder** (fn 0x1400f0d90): zeroes a 128-byte header struct and sets int0 = 1 (type PING), int1 = 0 (no bytes follow), int2 = interval in **seconds** (the transmitter passes interval_ms / 1000), int3 = option (the transmitter passes 1). So header ints are: [0] type, [1] count of following bytes, [2] type-specific (ping interval s), [3] option. The on-wire header length is returned by a virtual call and is not yet read; 16 bytes is the working assumption.
- **Transmit** (fn 0x1400ef4e0): a buffer is sent either synchronously or as an overlapped send with a completion event; there is no extra framing at this layer beyond the header + body.
- **TCP listener** (fn 0x1400deb30): `WSAAccept` loop; every accepted socket is logged as `a.b.c.d:port`, pushed on a queue, and an event wakes the connection manager, which then performs the identification exchange. No authentication step appears in the accept path itself; any password (`EncryptionPassword` config key is present) is applied later [inf].
- Symbols for the buffer classes (`L5L6BufferDisplay`, `L6FrameDirtyRegions`, input sinks) are not present in the database, so their constructors have to be found by following vtable references, which static search could not do here.

## 10. Recommended way to finish the wire spec

Capture a live session instead of reading more code: the installed server plus any spacedesk client gives the exact header length, the 334-byte identification body, DISPLAY_SETTINGS, FRAMEBUFFER, FLOW_CONTROL_ACK and input payloads, the discovery prefix/reply and the TCP port in a few minutes of capture. Use the static findings above to label fields in the capture. This needs a client device or VM and a capture tool on the Windows host; it also needs consent from whoever owns the traffic (your own machines only).

## 11. Ports [obs, from the running service on the analysed host]

`spacedeskService.exe` listens on **TCP 0.0.0.0:28252** and **UDP 28252** (one UDP socket per adapter address). The UDP socket is the discovery responder (section 7.3); TCP is the session listener (section 9). Observed with `Get-NetTCPConnection` / `Get-NetUDPEndpoint`; not a protocol default I verified elsewhere.

## 12. Verified from a live capture [obs: packet capture of one TCP session, server 192.168.1.200:28252 <-> one client]

The capture started **mid-session** (no SYN, no identification, no UDP discovery, no input events), so only steady-state video was seen. Streams reassembled with no gaps; both directions parsed exactly into whole messages (8,899,155 and 52,224 bytes, zero remainder).

### 12.1 Framing (verified)

- Every message = **128-byte header + `count` body bytes**. Header = 32 little-endian uint32; int[0] = message type (section 7.1), int[1] = body byte count.
- No other framing, length prefix, checksum or delimiter was seen. TCP was plain (not TLS) in this session.

### 12.2 DISPLAY_FRAMEBUFFER (type 2), server -> client (verified field values)

| Header int | Value seen | Meaning                                                                                 |
| ---------- | ---------- | --------------------------------------------------------------------------------------- |
| 0          | 2          | type                                                                                    |
| 1          | varies     | body size in bytes                                                                      |
| 2          | 1560       | frame width px                                                                          |
| 3          | 720        | frame height px                                                                         |
| 4          | 6240       | = width*4 (stride of the BGRA source)                                                   |
| 5          | 21         | constant, meaning [unk]                                                                 |
| 6, 7       | 0, 0       | region origin x, y [inf]                                                                |
| 8, 9       | 1560, 720  | region width, height [inf: whole frame here]                                            |
| 10         | 4          | constant [unk] (bytes per pixel?)                                                       |
| 11         | 2          | constant [unk] (encoding id? server enum for H.264 was 5, so probably a different enum) |
| 12         | 60         | constant [unk] (target fps or quality)                                                  |
| 13         | 4          | constant [unk]                                                                          |
| 15         | 1          | constant [unk] (fragment count?)                                                        |
| 17         | varies     | repeats int[1] (body size)                                                              |
| others     | 0          |                                                                                         |

- Body = **H.264 Annex B**, 4-byte start codes, every access unit begins with an AUD (NAL 9). 54 of 408 frames were IDR (NAL order AUD, SPS 7, PPS 8, IDR 5, so parameter sets are repeated on every keyframe); 354 were P frames (AUD, slice 1). SPS bytes `67 4d 40 2a` = profile_idc 77 (Main), level_idc 42 (4.2) in the first frame.
- One message = one whole access unit for the whole 1560x720 area in this capture. Partial-region updates and fragmenting were not exercised (screen was likely static); region/fragment encoding stays [unk].
- Decoding of the extracted stream was **not** run (no ffmpeg here); NAL structure is verified, decodability is not.

### 12.3 FLOW_CONTROL_ACK (type 7), client -> server (verified)

- 128-byte header, int[0] = 7, everything else 0, no body.
- Exactly **one ACK per framebuffer message** (408 and 408). It acts as a credit: the server can be expected to wait for ACKs before sending too far ahead [inf; window size unknown, needs a capture with a stalled client].

### 12.4 Still unverified

Identification exchange and its 334-byte body, PING, DISPLAY_SETTINGS / EVT_DISPLAY_SETTINGS / VISIBILITY / ROTATION, ERROR, input events (none sent during the capture), UDP discovery payloads, TLS/SSL variant, meaning of header ints 5, 10-13, 15.

## 13. Second capture: handshake, discovery, input [obs: one full session, Android phone client, plain TCP]

Client was a touch device (model string `SM-S928B`), so mouse/keyboard/pen events were **not** exercised.

### 13.1 UDP discovery (verified)

1. Client broadcasts to `255.255.255.255`/subnet broadcast **UDP 28252** the 21-byte ASCII payload `SPACEDESK-NET-CLIENT\0`. Repeated (220 sends seen).
2. Server replies unicast from UDP 28252, **308 bytes**:
   - bytes 0-259: host name, UTF-16LE, zero padded (here `Flight1`)
   - offset 260: uint32 = **28252** (TCP session port)
   - offset 264: uint32 = 10 [unk; Windows major version?]
   - offset 272/276/280: uint32 = 4, 8, 43 [unk; looks like a version]
   - rest zero.
3. This matches the binary: the listener compares the datagram prefix with a configured string and `sendto`s a fixed reply buffer.

### 13.2 TCP session start (verified)

Client opens TCP to server:28252 and speaks first:

| #   | Dir  | Type                  | Notes                                    |
| --- | ---- | --------------------- | ---------------------------------------- |
| 1   | C->S | 0 IDENTIFICATION      | body 334 bytes (below)                   |
| 2   | S->C | 4 DISPLAY_SETTINGS    | header only, all ints 0 except type      |
| 3   | S->C | 3 DISPLAY_VISIBILITY  | int[3] = 1                               |
| 4   | S->C | 3 DISPLAY_VISIBILITY  | int[2] = 1, int[3] = 1                   |
| 5-6 | S->C | 1 PING                | all ints 0 except type                   |
| 7.. | S->C | 2 DISPLAY_FRAMEBUFFER | first is IDR with SPS/PPS (section 12.2) |
|     | C->S | 7 FLOW_CONTROL_ACK    | one per frame (2638 = 2638)              |

### 13.3 IDENTIFICATION (type 0, client -> server)

Header ints (others 0): `[1]=334 [2]=4 [3]=8 [5]=3 [6]=3 [7]=2 [8]=60 [9]=0x00010005 [11]=0x0004003C [12]=1 [13]=1560 [22]=720 [31]=1`.

- `[13]`,`[22]` = requested/client display **width, height** (1560x720); the server then streamed exactly that size [inf].
- `[8]` = 60 = frame rate; the server echoes 60 in frame header int 12 [inf].
- `[9]` and `[11]` look like packed version/capability words (0x10005 = 1.5?, 0x4003C = 4 and 60?) [unk].
- Other ints ([2],[3],[5],[6],[7],[12],[31]): meaning [unk]; copy the observed values for compatibility until tested.
- Body, 334 bytes, UTF-16LE, zero padded: `{GUID-string}\0` (device id, 38 chars) at offset 0, then the device model string (`SM-S928B`) at offset 78. Remaining bytes zero in this capture.

### 13.4 EVT_INPUT_TOUCH (type 12, client -> server, verified fields)

128-byte header, no body, 175 events. Header ints: `[2]=x [3]=y [4]=client width [5]=client height [6]=phase [7]=timestamp_ms`.

- Coordinates are in the client space declared by [4],[5] (1560x720).
- Phase: **3 = begin**, **1 = move**, **5 = end** (always in the order 3, 1..1, 5 per gesture).
- `[7]` is a monotonically increasing millisecond counter (about 16-17 ms between moves = 60 Hz).
- Multi-touch was not exercised; no pointer id field was seen, so multi-touch encoding is [unk].

### 13.5 Updated status

Enough is now verified to build a compatible **server** for a touch client (discovery reply, identification, settings/visibility/ping preamble, H.264 frames with ACK pacing, touch events). Still unverified: mouse, keyboard, pen, rotation, DISPLAY_SETTINGS and EVT_DISPLAY_SETTINGS payloads, ERROR, partial regions, multiple displays, SSL variant, ACK window size, PING interval, audio link.

## 14. Live test against the real Android client [obs: reference server on Windows, phone SM-S928B]

Setup: vendor service stopped, `tools/video-oracle/ref_server/server.py` on UDP+TCP 28252 (Windows Python, because WSL2 here only forwards loopback; LAN discovery never reached it).

- **Discovery accepted**: the client listed `refserver` using the 308-byte reply from section 13.1 (verified).
- **Handshake accepted**: after identification (client sent 1560x720@60) and the preamble (settings, visibility x2, ping x2) the client stayed connected, ACKed ~1 frame per frame sent at ~50 fps with window 2, and sent touch events (verified).
- **Video**: an uncompressed I_PCM stream (1.7 MB/frame, Baseline 4.2) gave a **black screen** while the session stayed alive; a 4.4 KB/frame stream of I16x16 DC-predicted macroblocks (Main 4.2, same SPS/PPS shape as the vendor's) gave a **uniform gray** screen, and a DC-coefficient luma pattern (QP 51, deblocking off) gave a **visible gradient** (verified by the user's report). So the client decodes our Annex-B H.264 with AUD+SPS+PPS+IDR per message, one access unit per FRAMEBUFFER message, no P frames required.
- **New client message** seen once: type 6 (EVT_DISPLAY_SETTINGS) with header ints `[0]=6, [1]=1` (1-byte body) at the moment the user interacted with the app UI; body content not decoded yet [unk].
- Not exercised: P frames from our side, partial regions, rotation, mouse/keyboard.

## 15. Third capture: Windows client (PC `PCWORK`, 1440x900) [obs: one TCP session, 1330 frames, ~37 MB server->client]

Client was the vendor Windows app on another PC (192.168.1.201 -> server 192.168.1.200:28252). One TCP session only: reconnection and a resolution change were **not** captured. Message counts: client sent 1 IDENT, 6 PING, 1330 ACK, 5325 MOUSE; server sent 1 SETTINGS, 4 VISIBILITY, 10 PING, 1330 FRAMEBUFFER. No type 6, 9 or 11 appeared (keyboard, rotation and EVT_DISPLAY_SETTINGS remain unverified).

### 15.1 UDP discovery differences
- The client request is the same prefix `SPACEDESK-NET-CLIENT\0` but **padded to 1024 bytes** (broadcast to the subnet, port 28252); the phone sent 21 bytes. The server prefix match is therefore a prefix, not an exact length match [obs].
- Reply: same 308-byte layout as 13.1 (host name, uint32 28252, 10, then ints at 272/276/280 = 4, 8, 43). Int at 268 = 0 [obs].

### 15.2 IDENTIFICATION from a Windows client
Header ints: `[1]=334 [2]=4 [3]=8 [5]=2 [6]=3 [7]=2 [8]=70 [9]=0x00010005 [11]=0x0004001E [12]=1 [13]=1440 [22]=900`; `[31]` = 0 here (1 on the phone).
- `[5]`: 2 here, 3 on the phone [inf: client platform class].
- `[8]` = 70 and the server echoed 70 in every frame header int 12, so `[8]` is the requested frame rate [obs].
- `[11]` low 16 bits = 0x1E = 30 here, 0x3C = 60 on the phone [inf: capability/fps cap]; high 16 bits = 4 in both.
- Body: `{GUID}\0` then the host name `PCWORK` at offset 78, as in 13.3.
- The server streamed exactly 1440x900 (frame header ints 2/3/8/9).

### 15.3 Server preamble and frames
- Preamble identical to 13.2 (SETTINGS, VISIBILITY int[3]=1, VISIBILITY int[2]=1,int[3]=1, PING x2).
- The same 4-message group (VISIBILITY, VISIBILITY, PING, PING) was sent again mid-stream, after frame 655, with no client request and no frame size change [obs, trigger unknown].
- Server PING has two forms: all zero, and `[2]=15 [3]=1`, sent about every 15 s of idle while frames are still flowing [obs]. Client PING: `[0]=1 [3]=2`, 6 times [obs].
- Frames: header ints identical to 12.2 except `[1]`/`[17]` = payload size; every frame is one Annex-B access unit starting with an AUD (NAL 9). **P frames are used**: 165 IDR (AUD,SPS,PPS,IDR = NAL 9,7,8,5) and 1165 P (NAL 9,1). Unlike the phone capture, the delta frames can be as small as 53 bytes for a static screen.
- ACK: one type-7 header-only message per frame, same as 12.3.

### 15.4 EVT_INPUT_MOUSE (type 10, client -> server, verified fields)
128-byte header, no body. `[1]=0`; ints 4, 6 and above are 0 in all 5325 events.
- `[2]` = relative dx, `[3]` = relative dy, both **signed 32-bit** (two's complement), e.g. 0xFFFFFFFF = -1.
- `[5]` = flag bitmask, seen values: `1` move (4963 events), `0` (338 events, dx = dy = 0, meaning unknown), `2`, `4`, `8`, `16`, and combinations `3`, `5` (move + button flag, with dx/dy set).
- Button interpretation [inf from ordering]: `2`/`4` always occur as an alternating pair about 100-700 bytes apart (click), so `2` = left down, `4` = left up; `8` then `16` once, so `8` = right down, `16` = right up. Not confirmed with a labelled action by the user.
- No absolute position, no wheel field and no timestamp were seen. Wheel events were not exercised [unk].
- Rate: moves are only sent while the pointer moves; deltas are small (|d| <= ~5).

### 15.5 Still unverified after three captures
Keyboard (type 11), rotation (9), EVT_DISPLAY_SETTINGS (6) body, resolution change, reconnection, mouse wheel and middle button, pen, the 338 zero mouse events, ERROR, SSL variant, and why the server re-sent VISIBILITY/PING mid-stream.

The user reports that the Windows client app has no keyboard, rotation or resolution-change features, so those types are probably only reachable from a phone/tablet client [user-reported, not verified]. A capture with the Android app (rotation, on-screen keyboard if any) would be the way to exercise types 6, 9 and 11.

### 15.6 Fourth capture: phone keyboard and display events [obs: reference server log (`--logfile`), Android phone 192.168.1.24, 1560x720@60, plus a Windows client connected in parallel]
No packet capture: the server logged header ints and bodies of unknown client messages. All bodies were empty (header only).

**EVT_INPUT_KEYBOARD (type 11, client -> server, no body)**, header ints:
| Int | Meaning |
| --- | --- |
| 0 | 11 |
| 3 | character code (ASCII/Unicode code point), seen for lowercase letters and `.` (e.g. 116 = `t`, 46 = `.`) |
| 2 | virtual key code, used for special keys: 66 = Android `KEYCODE_ENTER` |
| 4 | kind flag: `4` = character in int 3; `65536` = key code in int 2 |
Only one message per key press was seen (no separate key-up). Characters came from the phone's on-screen keyboard. Int 1 was always 0. Unverified: Backspace, Shift, uppercase, non-ASCII, modifiers, key repeat (not exercised).

**EVT_DISPLAY_SETTINGS (type 6, client -> server, no body)**: ints `[0]=6, [2]=1`, sent once (08:26:12) after the user touched the app UI; the phone closed the session 5 s later. Section 14 recorded it as `[1]=1`; the field position there was likely misread, treat int 2 as the value seen here. Meaning of the value is still [unk].

**Reconnect quirk**: 16 s after the close, the phone opened a new TCP connection whose **first message was type 6** (count 0), not IDENTIFICATION; our server rejected it as a bad identification. The phone then reconnected normally (ident 1560x720 again). So a server should be tolerant: accept a type 6 before identification (likely a "display settings changed / reconnect" hint) instead of failing [inf].

**Type 8 (client -> server, header only, `[0]=8`)** seen once, 30 s before the session closed (08:27:38). Unidentified [unk]; possibly a keep-alive or an app-level close/notify.

**Not observed**: type 9 (rotation). The identification size did not change between sessions (1560x720), so no rotation or resolution change reached the server in this run (either the app locked the orientation or the options used do not message the server).

### 15.7 Fifth capture: Windows client (192.168.1.201, 1440x900@70) against the reference server [obs: `--logfile`, 08:49-08:51]
Session of ~2 min, 2657+ frames sent and ACKed, no errors. The client sent 3438 type 10 and 5 type 11 messages; **no type 6, 8 or 9**, so the Windows client does reach keyboard (11) but not rotation or display settings. This corrects 15.5: keyboard does exist on the Windows client [obs]; rotation and resolution change still [unk] (no UI action produced a message).

**Type 11 from Windows uses different fields than the phone (15.6)** [obs, meaning inferred]:
| Int | Meaning |
| --- | --- |
| 2 | Windows virtual-key code (27 = Esc, 18 = Alt/VK_MENU, 9 = Tab) |
| 3 | hardware scan code (1 = Esc, 56 = Alt, 15 = Tab) |
| 4 | `2` present = key up (KEYEVENTF_KEYUP); absent = key down |
Seen: Esc down+up, Alt down (no up logged), Tab down+up. Fits the `{code, flags}` handed to `SendInput` (section 16/17). The phone's `4`/`65536` kind flags and the PC's key-up flag share int 4, so a server must branch on the value [inf]. Modifiers held, extended keys and repeat remain unexercised.

**Type 10 additions**: flag values now also include `9` (move + `8`) and `4` as the most common button flag after `1` (243 events); dx/dy ints are omitted when zero, and 83 events carry only `[0]=10` (the "zero events" of 15.4). The 15.4 claim that deltas are small (|d| <= ~5) is wrong for this client: 791 values exceed 20 in magnitude, so do not clamp.

### 15.8 Client message handlers in the service [obs: static, `OnPacketReceived_` 0x1400ee690 and `L5ThreadReceiver::Run` 0x1400ed190; not yet confirmed live]
Header ints are the same indexes used in the logs above.
| Type | Handler behavior |
| --- | --- |
| 9 ROTATION | `int[2]` = rotation value (see 15.9: the phone sends 1..3, i.e. a 1-based enum, **not** degrees; the registry `Rotation` is a separate value in degrees, reduced modulo 360). Stored in the client record, then a virtual call applies it (the tray's DISPLAY_ROTATION_SET, section 17, uses the CCD API). Always returns "handled". |
| 6 EVT_DISPLAY_SETTINGS | `int[2]` = **stream state**: `1` = start/resume sending frames, `2` = stop/pause. A repeat of the current state is logged and ignored; any other value is logged as invalid. So the phone's `[2]=1` seen in 15.6 means "stream on" (app came to foreground), not a setting [inf]. |
| 8 ERROR (client -> server too) | `int[2]` = Reason; the receiver logs it, **ends the session**, and records disconnect type 1 if Reason = 1, 2 if Reason = 2, else 0. So the type 8 in 15.6 was a client-initiated close with Reason 0 (zero ints are not printed) [inf]. |
| 11 KEYBOARD | `int[4]` flags: bit `4` set -> character input, code in `int[3]`; else bit `0x10000` clear -> VK in `int[2]`, scan code in `int[3]`, flags in `int[4]` (key up = 2); else bit `0x10000` set -> key code in `int[2]` (Android code path). Matches 15.6 and 15.7. |
| 12 TOUCH | contact id = `int[8]` + 1; ints 2, 3, 4, 5, 6, 7 and a body at ints `+9` (layout in section 13). |
| 10 MOUSE | ints 2 (dx), 3 (dy), 4, 5 (flags), 6 passed through. |
| 7 ACK, 1 PING | PING with `int[3]` = 2 is a keep-alive reply that refreshes the last-seen time. |

**Rotation is client-driven.** The server never announces an orientation. The phone sends type 9 and the service rotates the Windows display for that client; the frame size does **not** change (corrected in 15.10; an earlier version of this note said width and height swap, which was wrong).

### 15.9 Sixth capture: Android phone over TLS-PSK against the reference server [obs: `--psk`, `--logfile`, SM-S928B 192.168.1.24, 10:44-10:46]
**Transport encryption** [obs]. With `EncryptionSslEnable=1` the service speaks **TLS 1.3 with an external pre-shared key**, same TCP port, no certificate needed:
- ClientHello: cipher `TLS_CHACHA20_POLY1305_SHA256` (0x1303), `psk_key_exchange_modes = psk_dhe_ke` only, x25519 key share, PSK identity `Client_identity`. Because of the ECDHE share, a packet capture **cannot** be decrypted even with the password (forward secrecy); to read the traffic the reference server terminates TLS itself.
- PSK = `hex_decode(EncryptionPassword + "1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A")`, i.e. 20 bytes for the 8-hex-digit password (fn 0x14008ca50 builds the string, 0x1400f99e0 is OpenSSL's hex-to-buffer). Verified offline: the PSK binder of the phone's ClientHello in a capture against the vendor service matches this key and none of 20 other candidates (raw ASCII, hex without suffix, hashes, UTF-16).
- Android decides plain or TLS before connecting. When the app asks for a password (keyboard and rotation require it), the traffic is TLS. A plain connection to a server that announces TLS in discovery is not made; our server also served plain when the first byte was not 0x16, and the phone's behavior then was to open extra plain connections that never ACKed and to retry. The discovery reply of the vendor service differs from ours at byte 280 (`0x3B` vs `0x2B`): bit `0x10` is set when TLS is enabled [inf, not isolated].

**Rotation, type 9** [obs]: only `int[2]` is set. Values seen: 1, 2, 3 (one at connect, one per orientation change, sometimes repeated within a second). Meaning and server behavior: see 15.10.

**Keyboard from Android, type 11** [obs] (extends 15.6, mapped to the handler in 15.8):
| `int[4]` | `int[2]` | `int[3]` | Meaning |
| --- | --- | --- | --- |
| 4 | 0 | code point | typed character, any Unicode value (`a`=97, `C`=67, `.`=46, `1`=49, `0`=48, `\`=92, `{`=123, `<`=60, `*`=42, `£`=163, `€`=8364, `○`=9675, `♡`=9825, `《`=12298). Shift is not sent: the uppercase character arrives as such. |
| 65536 | Android key code | 0 | non-character keys: 67 = `KEYCODE_DEL` (Backspace), 66 = `KEYCODE_ENTER`, 62 = `KEYCODE_SPACE` |
No key-up message exists on this path (one message per key). Backspace, Enter and Space always use the key-code form, never a character. Other key codes (arrows, Tab, Escape, Delete, function keys) were not exercised; the server should map Android `KEYCODE_*` values to Windows virtual keys [inf].
Types 6, 8, 10 and 12 did not appear in this session.

### 15.10 Rotation end to end [obs: static, service `OnPacketReceived_` 0x1400ee690, `SE_MAIN_CMD_SEND_ROTATION` in 0x1400aec10, `OnEvtDeviceDisplayRotation` 0x14008c2f0, mouse/touch mapping 0x1400a5be0, frame header 0x1400f1250; tray 0x406af0. Live result with the phone in 15.10.1]
- **Value** = `DISPLAYCONFIG_ROTATION`: 1 identity, 2 rotate 90, 3 rotate 180, 4 rotate 270. The service stores it per client, the main handler reads and clears it (-1 = none), looks up the client's monitor (adapter LUID + target id; fails with "Display monitor is not attached" otherwise) and asks the tray (command 3 / pipe message DISPLAY_ROTATION_SET) to apply it. The tray writes the value unchanged into `targetInfo.rotation` and calls `SetDisplayConfig`.
- **Nothing is sent back to the client** for a rotation, and the DISPLAY_FRAMEBUFFER header has no rotation field (below). The stream is not restarted.
- **The frame keeps the identified size and orientation.** Evidence: the input mapper converts client coordinates (frame space, unrotated) to the desktop with a per-rotation transform: r=1 `(x, y)`; r=2 `X = W - y*W/ch, Y = x*H/cw`; r=3 `X = W - x*W/cw, Y = H - y*H/ch`; r=4 `X = y*W/ch, Y = H - x*H/cw` (cw, ch = client frame size; W, H = desktop size of that monitor). So Windows rotates the desktop *inside* the native-orientation frame, and the client turns the picture when it draws it. A server must therefore keep sending W x H frames and only rotate the content; it must apply the same transform to touch and absolute mouse input.
- Sending frames with swapped width/height after a rotation is wrong: the phone stops ACKing and does not recover even when the old size returns (three live attempts, 15.10.1).
- With the phone identifying as landscape (640x480), value 1 is the landscape position, 2 portrait, 3 the opposite landscape; 4 (portrait upside down) is never sent because Android does not rotate to it [inf from the logs].

**DISPLAY_FRAMEBUFFER header, from the builder (0x1400f1250)**, replaces the guesses of 12.2: `[2]` width, `[3]` height, `[4]` pitch, `[5]` = 0x15 constant, `[6..9]` = update rectangle **left, top, right, bottom** (0, 0, W, H for a full frame, so 8/9 are right/bottom, not width/height), `[10]` = encoding code derived from the section EncodingType (1 -> 0; 2 or 3 -> 1; 5 (H.264) -> 4; other -> 3), `[11]` and `[12]` = two caller values (seen 2 and 60), low 16 bits of `[13]` = the same encoding code, `[15]` = fragment count (low 16 bits), `[16]` = 1 if fragment count != 1, `[17]` = body size. The log string names the first four as `W/H/P/F`.

#### 15.10.1 Live attempts against the phone (reference server)
1. Server ignores type 9, frame 640x480: stream keeps flowing (6027 sent / 6025 ACKed across 4 rotations); picture not rotated by the server.
2. Server swaps the frame to 480x640 (alone, with a VISIBILITY/PING group, with DISPLAY_SETTINGS, with 12 or 40 frames of extra credit): ACKs stop within a few frames every time and stay stopped after returning to 640x480.
3. Server keeps 640x480 and rotates the test pattern with the 15.10 transform (12:54): values 1, 2, 1, 2, 3 received, 2036 frames sent / 2034 ACKed, no stall at any rotation. User observation: the bar kept running left to right from the viewer's side in every position (landscape, portrait, opposite landscape), so the phone turns the picture and the 15.10 transform has the right sense for values 1, 2 and 3 (a rotation is fixed by one axis; value 4 not reachable on this phone). The corner marker was not noticed by the user, so it adds no evidence. **Rotation is resolved for the server side.**

### 15.11 Keyboard paths in the service and the key table [obs: static, 0x1400ee690, commands 0x14-0x16 in 0x1400aec10, `SEInputKeyboardMapping::ScanCodeGet` 0x1400babe0, table at VA 0x1404017d0]
Type 11 is routed by `int[4]` (15.8) into three commands:
- **Unicode** (`int[4]` bit 4, Android/iOS typed text): the code point in `int[3]` goes to the tray with flag 4 (`KEYEVENTF_UNICODE`), injected with `SendInput`.
- **Software scan code** (`int[4]` bit 0x10000, Android/iOS non-character keys): the client key code in `int[2]` is looked up in a table by client OS type (3 = Android `KEYCODE_*`, 4 = iOS HID usage); the resulting scan code goes to the tray with flag 8 (`KEYEVENTF_SCANCODE`), plus flag 1 (`KEYEVENTF_EXTENDEDKEY`) when the entry is marked extended. Unknown codes give 0 and are dropped.
- **Hardware** (neither bit, Windows client): scan code from `int[3]` (OS types 1 and 2; `0xE000` is OR-ed in when flags bit 0 is set), flags from `int[4]` (2 = key up), written to the virtual HID keyboard device, not through the tray. Fails with a log line when that device is missing or not started.

The table has 114 rows `{Android key code, iOS HID usage, Windows VK, scan code, extended}`; it is saved as `keymap.csv` next to this file (interface data read from the installed binary). Examples: Android 67 (DEL) -> 0x000E Backspace; 66 (ENTER) -> 0x001C; 62 (SPACE) -> 0x0039; 19/20/21/22 (DPAD) -> 0xE048/0xE050/0xE04B/0xE04D arrows; 61 TAB -> 0x000F; 111 ESCAPE -> 0x0001; 112 FORWARD_DEL -> 0xE053; 131-142 F1-F12 -> 0x3B.. ; 57/58 ALT, 59/60 SHIFT, 113/114 CTRL. This closes the "other key codes" gap of 15.9 without a live test; The tray (message 14, 0x402590) always injects a **press and a release in one `SendInput` call**: Unicode path = 2 inputs (`KEYEVENTF_UNICODE`, then `UNICODE|KEYUP`), or 4 inputs as a surrogate pair when the code point is above 0xFFFF; scan-code path (flag 8) = 2 inputs (given flags, then flags | `KEYEVENTF_KEYUP`). So phone keys never stay held and modifier combinations from the phone are not possible on these paths [obs].

## 16. spacedeskConsole.exe: how it manages and observes the service [obs: static IL of the console, v1.1.1800.103, .NET Framework 4.7.1, x86 AnyCPU]

Method: PE/CLI metadata and CIL decoded without running the target (REA `inspect_managed_members` plus a metadata-only IL dump). VideoWall code was ignored on purpose. No decompiled C# exists; statements below come from call/ldstr sequences, so "[inf]" marks reading between instructions. The service binary was only searched for the same value names (UTF-16 strings), not decompiled again.

### 16.1 Architecture: no IPC to the service [obs]
The console has **no pipe, socket or RPC link to spacedeskService**. It uses four channels:
1. **Registry** `HKLM\SOFTWARE\datronicsoft\spacedesk` (64-bit view forced with `RegistryView.Registry64` on 64-bit OS): settings in, per-client state out.
2. **Windows Service Control Manager** through `ServiceController` (status, start type, Start/Stop with `WaitForStatus`) and `sc config`.
3. **WMI**: `Win32_PnPEntity` (driver devices), `root\SecurityCenter2` (AV/firewall), event queries for change notification.
4. **Child processes**: `reg.exe`, `logman.exe`, `pnputil`, `dxdiag`, PowerShell `Get-NetConnectionProfile`, DebugView.
Service-side check [obs]: `spacedeskService.exe` contains the same UTF-16 names (`EncryptionSslEnable`, `EncryptionPassword`, `NetworkConnectionEnabled`, `AndroidUsbControlEnabled`, `IosUsbControlEnabled`, `ClientConnected`, `AdapterLUID`, `VideoPresentTargetID`, `ClientDescription`, `CompressionQuality`, `FramesPerSec`, `DisconnectType`, `Volatile`, `ConnectionLossDelayMS`, `DebugLevelAPI`). So the service is the writer of per-client state and the reader of the settings.

### 16.2 Settings (all REG values, hive HKLM) [obs]
`...\spacedesk\Service`:

| Value | Meaning | Default when absent |
| --- | --- | --- |
| `NetworkConnectionEnabled` | LAN transport on; `"0"` = off | on |
| `AndroidUsbControlEnabled` | Android USB cable; on only if `"1"` | off |
| `IosUsbControlEnabled` | iOS USB cable; on only if `"1"` | off |
| `EncryptionSslEnable` | SSL + password; `"0"` = off | off |
| `EncryptionPassword` | 8 hex digits from `System.Random`, **stored plain** (REG_SZ) | generated when empty |
| `ConnectionLossDelayMs` (also writes legacy `ConnectionLossDelay`) | ms; on a bad or missing value the console writes 1000 | 1000 |

`...\spacedesk\Server\ServerStartType` (console-side mirror, **not** read by the service): `1` = **manual**, `0` or absent = automatic (confirmed in decompiled C#, `IsSpacedeskStartTypeAutomatic` returns false only for `"1"`; the toggle writes `checked ? 0 : 1`). `...\spacedesk\DiagnosticEnabled` and `...\spacedeskConsole\{LastWindowPositionX/Y, LastWindowSizeX/Y, DebugTraceEnabled}` are console-only. Console reads `Service`-values only when the service is running for USB flags and VideoWall. Changing a setting works by **write then restart service** (`Stop`, wait, `Start`): LAN, USB, encryption and password changes all call `spacedeskServerStartStop` with a progress bar, so the service reads these at start [inf: no live reload seen].

### 16.3 Start at System Boot [obs]
Toggle handler `CBServerStartType_Toggle`:
1. Writes `C:\Users\Public\TempSpacedeskConfig.cmd` with `sc config spacedeskService start=auto` (on) or `start=demand` (off), runs it through `cmd.exe` with verb `runas`, then deletes the file.
2. Writes `Server\ServerStartType` in the registry.
The click handler first checks `WindowsPrincipal.IsInRole(Administrator)`. If not elevated it shows a dialog and relaunches itself with verb `runas` and argument `RestartAsAdminShowDefault`. The real state is read from `ServiceController.StartType` (shown as Automatic/Manual); the registry value is only a mirror [inf]. Note "Manual" is `demand`, never `disabled`. A new app should read the SCM start type as the source of truth and use the `ChangeServiceConfig` API instead of a temp .cmd file.

### 16.4 Connection status [obs]
- **Per-client records**: each client is a subkey `...\spacedesk\<GUID>` (GUID = identification device id, section 15.2). Values: `ClientDescription`, `DeviceType`, `OsType` (enum ClientOSType), `LicensingType`, `EncodingType` (1 = RGB uncompressed, 4 = software, 5 = hardware), `Width`, `Height`, `OriginX`, `OriginY`, `Rotation`, `Contrast`, `Brightness`, `CompressionQuality`, `MirrorFlip`, `FramesPerSec`, `ConnectionDate`, `ConnectionTime`, `DisconnectionTime`, `DisconnectType`, `DisconnectInfo`, `DisplayMode` (`Extension`, `Duplication`, `Detached`, `Disconnected`), `AudioClientConnected`.
- **Live flag**: `...\spacedesk\<GUID>\Volatile\ClientConnected` = `"1"` means connected; the same `Volatile` key holds `AdapterLUID` and `VideoPresentTargetID` (the display connector the client maps to). Volatile keys disappear on reboot.
- **Disconnect reasons** (`DisconnectType`/`DisconnectInfo`): Disconnected properly; Viewer H.264 decoder init failed; Viewer H.264 decoding failed; Render graphics adapter out of memory; Invalid packet received; H.264 encoder init failed; H.264 encoding failed; Connection Loss; Connection Timed Out. Enum values are now known from the decompiled console (`research/vendor-reference/console/SConsole.Enum.*.cs`).
- **TCP view**: `IPGlobalProperties.GetActiveTcpConnections()` filtered to local endpoint containing port `28252` and state `Established` (5), matched to a client record by remote IP in `ClientDescription` [inf], plus audio flag from `AudioClientConnected`.
- **Display mode** is derived with the Windows CCD API (`QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`) for the client's `VidPnTargetID`/LUID: extended, duplicated or detached.
- **Change notification**: WMI `RegistryTreeChangeEvent` on the spacedesk key refreshes the client list; `__InstanceOperationEvent` on `Win32_Service` (spacedeskService), `Win32_Process` (Apple Mobile Device) and `Win32_PnPEntity` (driver devices); .NET `SystemEvents.DisplaySettingsChanged`, `NetworkChange.NetworkAddressChanged`, `SystemEvents.PowerModeChanged`.
- **Network adapters panel**: `NetworkInterface.GetAllNetworkInterfaces()` (name, description, type, speed in Mbps, IPv4 address), network category from `Get-NetConnectionProfile` (Public/Private), VPN detection when interface type is `Ppp`, `Tunnel` or 53 (proxy virtual).
- **Service status**: `ServiceController.Status`, version from the file version of `spacedeskServiceTray.exe` next to the console.

### 16.5 Diagnostics [obs]
- **Runtime collection ON**: writes `C:\Users\Public\spacedeskDebugStart.cmd` and runs it elevated: `reg add` of `DebugLevel`, `DebugLevelAPI=11`, `DebugLevelSRV=87`, `DebugLevelCUA=15`, `DebugLevelCUI=15`, `DebugLevelCNE=15`, `DebugLevelCSE=21`, `DebugLevelTAP=9`, `DebugLevelIMG=5`, `DebugLevelPLS=1` under `HKLM\SYSTEM\CurrentControlSet\Services\spacedeskService`; starts SysInternals **DebugView** (`Dbgview.exe /t /o /g /l <log>`, downloaded from `download.sysinternals.com` on demand to `C:\Users\Public\Downloads\DebugView`); starts 7 ETW sessions with `logman create trace <name> -o <etl> -ets -ow -mode sequential -p {GUID} <flags> <level>`: spacedesk IddCx `{5ddc8611-bcd7-49f6-a44e-29ddc9e028a4}`, Windows IddCx `{D92BCB52-FA78-406F-A9A5-2037509FADEA}`, Bus `{06387C06-0B22-47DB-A877-A90CC880EF07}`, HID `{453bf499-835f-42ca-8a67-a9aa549a185b}`, Audio `{0F037BF4-C7C2-4A49-BECC-7B8DF2BE6739}`, Android Control `{07bae272-98c7-48fc-abc8-55d06748289f}`, Android USB `{8a9bfe4c-4b1e-4e02-8bab-39c5faaf81d8}`. Sets `DiagnosticEnabled=1`. The service reads the `DebugLevel*` values [inf: it contains `DebugLevelAPI`].
- **OFF**: `reg delete` of those values, `logman -stop <name> -ets` for each session, kills DebugView, `DiagnosticEnabled=0`.
- **Save All Information** copies into `yyyy-MM-dd-HHmmss-spacedeskRuntimeDiagnostics`: DebugView log, the 7 ETL files, `spacedesklist.xml`, `sp_connectionlist.xml`, `C:\Windows\INF\setupapi.dev.log`, `spacedeskSetup.log`, WDF `*.dmp` from `ProgramData\Microsoft\WDF`, crash dumps (`CrashDumps` in the user and in `System32\Config\SystemProfile`), `C:\Windows\Minidump`, `pnputil -e` (`PnpUtilInfo.txt`), `dxdiag /dontskip /whql:off /t`, `reg export HKLM\SOFTWARE\datronicsoft spacedeskregistry.txt`, and a `spacedeskDiag.txt` with status, troubleshoot text and runtime logs.
- **Status text** ("Diagnostics Info") = driver/service version, service status and start type, encryption, loss delay, interface switches (LAN, Android USB, iOS USB), adapters, OS info (`Win32_OperatingSystem`), driver inventory (`Win32_PnPEntity` where manufacturer is datronicsoft, with file versions in `System32\drivers` and `drivers\UMDF`).

### 16.6 Troubleshoot checks [obs]
Incompatible software or drivers (Splashtop, MCT, FrescoLogic, CH products, Amyuni USB Mobile Monitor, Samsung USB Driver for Mobile Phones, Nvidia ShadowPlay overlay) from services and PnP; active VPN; third-party antivirus, firewall and antispyware from `root\SecurityCenter2` (`AntivirusProduct`, `FirewallProduct`, `AntiSpywareProduct`); client records with `DisplayMode=Detached` (user must switch to Extend or Duplicate in Windows Display Settings); missing driver files; PnP devices of the vendor with `Status=Error` and `ConfigManagerErrorCode` mapped to text; iOS USB needs `Apple Mobile Device Service` running and the `AppleInc.iTunes` or `AppleInc.AppleDevices` package. "Clear spacedesk Settings" deletes `HKLM\SOFTWARE\datronicsoft\spacedesk`; "Clear Windows Display Settings" deletes `...\spcdsk\ConnectorsAdapter0`, `...\v3DDK\ConnectorsAdapter0`, `...\v3DDK\Enum`, then `reg delete` of `GraphicsDrivers\Connectivity` and `Configuration`. Export uses `reg.exe export HKEY_LOCAL_MACHINE\SOFTWARE\datronicsoft spacedeskConnectionSettings.reg`.

### 16.6b Enum values [obs: decompiled C#, `research/vendor-reference/console/SConsole.Enum.*.cs`]
- `ClientOSType`: 0 Unknown, 1 WindowsNative, 2 WindowsUWP, 3 Android, 4 iOS, 5 Undefined (registry `OsType`).
- `LicenseType`: 0 None, 1 NonCommercialBasic, 2 NonCommercialAdvanced, 3 BusinessAndroid, 4 BusinessWindowsUWP, 5 BusinessIOS (`LicensingType`).
- `spacedeskDisconnectType` (`DisconnectType`): 0 Client, 1 Server, 2 ConnectionLoss, 3 ConnectionTimeOut.
- `spacedeskClientDisconnectReason` (`DisconnectInfo` when type is Client): 0 DisconnectedGracefully, 1 DecoderH264InitFailed, 2 DecodingH264Failed.
- `spacedeskErrorServer` (`DisconnectInfo` when type is Server): 0 None, 1 GpuNoMemory, 2 ReservedUnused, 3 InvalidPacket, 4 EncoderH264InitFailed, 5 EncodingH264Failed.
- `EventNotificationType`: 1 RegistryChange, 2 NetworkChange, 3 ServiceStatusChange, 4 DisplaySettingsChange, 5 DeviceStatusChange, 6 PowerModeChange.
- Correction of an earlier reading: `ServerStartType` is **0/absent = automatic, 1 = manual** (see 16.2).

### 16.6c Settings reload in the service [obs: static, `research/vendor-reference/service/functions`; not live-tested]
The service reloads settings without a restart. `SIThreadRegistryNotification::Run` (0x1400933c0) opens `HKLM\Software\datronicsoft\spacedesk`, calls `RegNotifyChangeKeyValue` (subtree, filter NAME|LAST_SET) through `PLSSettingsChangeNotification` (0x1400d50f0), and on each signal re-arms the watch and calls a virtual method that submits notification priority 2 to the main event handler (`SEThreadMainEventHandler::Run`, 0x1400aec10, log string "PLA_NOTIFICATION_PRIORITY_02 (Settings change)"). That handler re-reads the settings; if one flag differs from the cached value it stops the communication service, updates its state and starts it again (existing connections of that listener drop). Per-value getters such as `ConnectionLossDelayMS` (default 45000) read the registry on every call, so those take effect immediately. Inference: the console's restart of the service is not required for most settings; a live test (change a value, observe) would confirm which ones need the listener restart. For the new app: watch the key (or own config store) and apply in place; restart only the listeners when LAN/USB/encryption change.

### 16.7 Unknowns
`ConnectionCloseDelay` is a VideoWall value and was skipped; the console runs nothing against the service wire protocol, so none of this touches sections 12-15.

## 17. spacedeskServiceTray.exe: user-session helper and tray UI [obs: static analysis in Ghidra 12.2, native x86, build "Nov 5 2026", driver string v2.2.35]

Not .NET (unlike the console). It has two roles: the **tray icon and menu**, and a **helper that runs in the logged-in user's session** for things a Windows service in session 0 cannot do. Internal name: `STxxx` classes, log prefix `ST:`. The registry key `...\spacedesk\DebugLevelTAP` (value 9 during diagnostics, section 16.5) controls its logging. VideoWall menu items exist and were ignored.

### 17.1 IPC with the service: named pipe [obs]
- `\\.\pipe\spacedeskServiceTrayPipe`, created by the tray with `CreateNamedPipeW`: duplex, **message mode** (`PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE`), up to 255 instances, 1024-byte buffers, **NULL DACL** (any local process can connect and write). A thread per connection. Security note for the new app: use an explicit ACL (SYSTEM + the interactive user) instead.
- Request framing: **8-byte header** `uint32 MessageId, uint32 DataSize` followed by `DataSize` bytes (log text: `RX HDR: ---> <name> <id> (<n> byte data)`). The first message on a connection is CONNECT with a payload whose value is a connection type; type 0 starts the per-connection thread [inf].
- Responses to GET messages are **raw bytes with no header**, written with `WriteFile`; SET messages have no response.
- Message IDs (confirmed from the id-to-name switch, which is not in address order):

| Id | Name | Payload / effect |
| --- | --- | --- |
| 0 | CONNECT | connection type |
| 1 | TRAY_ICON_SET | `uint32 iconId` -> window message `0x8014`; also refreshes display info |
| 2 | TRAY_BUBBLE_SET | balloon tip (title + text, `STIconManager::SetBalloonTip`), message `0x8015` |
| 3 | TRAY_BRANDING_SET | message `0x801E`; strings "spacedesk DRIVER (v2.2.35)", "Commercial and Non-Commercial" |
| 4 | MENU_ACTIVATE_MENU1_HOOK_STANDARD | message `0x8016` |
| 5 | MENU_CHANGE_EVENT_GET | response 4 bytes: menu-change event value |
| 6 | MENU_ITEM_CLICKED_GET | response 4 bytes: which menu item was clicked (service polls) |
| 7 | MENU_ITEMS_STATE_SET | states of the fixed menu items |
| 8 | DEVICE_MENU_ITEMS_CLEAR | clears the connection-list vector (items of 0x210 bytes), message `0x8033` |
| 9 | DEVICE_MENU_ITEM_ADD | one 0x210-byte item (UTF-16 strings, one at offset 0x110), message `0x8033` |
| 10 | DISPLAY_CHANGE_EVENT_GET | response 4 bytes |
| 11 | DISPLAY_COORDINATES_GET | response: `uint32 count` then `count` records of 0x44 bytes (monitor rectangles, DPI-aware via `GetSystemMetricsForDpi`) |
| 12 | DISPLAY_ROTATION_SET | `{adapter LUID, targetId, rotation}` applied with the CCD API (`SetDisplayConfig`); skipped if already set or the target is in duplicate mode |
| 13 | DRIVER_TYPE_SET | 1 byte stored in a global |
| 14 | INPUT_EVENT_GENERATE | `{uint32 code, uint32 flags}` -> **`SendInput`**: keyboard scancode, or a Unicode code point (surrogate pairs above 0xFFFF); flag bit 8 selects the raw-flags path |
| 15 | MESSAGE_BOX_SET | code `0x98` or `0xA3` (+ bool) -> message `0x8034`/`0x8035`/`0x8036`, i.e. the expired-driver and unlicensed-viewer dialogs |

Consequence for the wire protocol (section 7): the service does **not** inject keyboard input itself; it asks the tray in the user session through message 14. Mouse and touch go through the service's virtual HID driver (section 7.5), so mouse worked in the Windows-client capture without any tray.

### 17.2 Tray UI [obs]
- Icon states: **ON (idle)**, **ON (connected)**, **OFF**, **ERROR**, **Reboot Required** (strings of the menu/tooltip; exact icon ids are [unk]). Tooltip/branding shows the driver version, "DRIVER for PRIMARY Machine (Server)", plus the idle/connected state and a count of connections.
- Menu command ids (low byte of `WM_COMMAND`, handler `FUN_00407840`):

| Cmd | Item | Action |
| --- | --- | --- |
| 1 | Windows Display Control Panel... | `WinExec("RUNDLL32.EXE SHELL32.DLL,Control_RunDLL desk.cpl,,3")` |
| 2 | Turn server ON | if the service is not running, **starts it through the SCM** (`StartService`), then flags a restart of the tray |
| 3 | Turn server OFF | confirmation box "Disable spacedesk SERVER"; on YES it only sets a flag; the **service** reads it with MENU_ITEM_CLICKED_GET and stops itself [inf] |
| 4, 5 | Input ON / Input OFF | set flags read by the service the same way [inf from menu order] |
| 6 | Clear All Display Settings | confirmation, then flag (service or console performs the clear) |
| 7 | spacedesk Driver Console... | launches `spacedeskConsole.exe` from the tray's folder |
| 8 | Hide Connection Window | toggles and writes `HKCU\Software\datronicsoft\spacedesk\TrayConnectionWindowHide` (DWORD) |
| 9 | About | message `0x8037` |
- Dynamic items: one per connected client (added with DEVICE_MENU_ITEM_ADD), shown in a "spacedesk connectionlist" window: columns **Machine Name IPAddress:Port** and **Client**, **Desktop** state (`unplugged`, `detached`, `attached (extend)`, `attached (mirror)`), text "No remote connection". A second list shows local network interfaces (adapter type and IPv4 from `GetAdaptersInfo`): "Network Interfaces (local)" / "Network Connections (remote)".
- Notifications: connection notification dialog "Connection(s) pending..." with the hint "To disable or enable spacedesk USB or network connections, use spacedesk Driver Console"; "Non-Commercial Viewer connected" with the licensing text; "spacedesk Display Driver has expired!" (the console also has an expiry date constant, 2027).
- Single instance via window classes `SPACEDESKSERVICETRAYAPPLICATION` and `...2`; reacts to `TaskbarCreated` to re-add the icon; named events `spacedesk.NotificationEventMenuChange` and `spacedesk.NotificationEventDisplayChange`.
- Service state: `OpenSCManager`/`OpenService`("spacedeskService")/`QueryServiceStatusEx`/`StartService`.

### 17.3 What the new app needs from this [inf]
1. A **user-session helper** (can be the app's own tray process) for: keyboard injection (`SendInput`), applying display rotation and reading monitor coordinates (CCD API), showing balloons and message boxes, and the menu. Keep the service-to-helper pipe, but with an ACL.
2. The tray menu actions in 17.2 map onto the console functions of section 16 (service on/off, settings, console launch), so the tray can call the same module instead of using flags and polling.
3. Unknown: exact payload struct of messages 1, 2, 3, 7 and 9, and icon ids. These only matter if compatibility with the vendor service is wanted, which is not the goal.

## 18. Reference material kept for the implementation

Decompiled output lives in `research/vendor-reference/` (index and feature-to-file map in `research/vendor-reference/README.md`). It is evidence, not source to copy: the new app is written clean-room from this spec, and the folder must not be committed or published.

| Component | Output | Status |
| --- | --- | --- |
| `spacedeskConsole.exe` (.NET) | `research/vendor-reference/console/*.cs`, 12 k lines of C#, VideoWall types removed | complete (all types decompiled without errors) |
| `spacedeskServiceTray.exe` (native x86) | `research/vendor-reference/tray/functions/0x<addr>.c`, 1207 functions, 157 externals skipped | complete |
| `spacedeskService.exe` (native x64) | `research/vendor-reference/service/functions/0x<addr>.c`, 9925 functions written, 356 externals skipped, 0 failed (96 MB) | complete; file names are the same image VAs (`0x1400...`) used in sections 1-11, e.g. `0x1400ec4c0.c` is the message-type dump routine, `0x1400e4460.c` the identification receive, `0x1400eee30.c` the transmitter thread |

How to use it while implementing:
1. Start from the SPEC section for the feature (settings 16.2, boot 16.3, status 16.4, diagnostics 16.5, troubleshoot 16.6, tray 17), then open the file named in `research/vendor-reference/README.md` to settle a detail.
2. Registry names, command lines, GUIDs and enum values are facts and may be reused; control flow and UI code are not to be ported line by line.
3. When a detail turns out to be wrong, fix it here first, then in the code (done once: `ServerStartType`).
4. Tray pipe messages 1, 2, 3, 7, 9 (payload layouts) and icon ids are still undecoded; `tray/functions/0x402590.c` is the entry point if compatibility is ever needed.

### 18.1 Open items after this pass
Section 7.5 input layouts (mouse done in 15.4; keyboard now known to be generated through tray message 14, so the wire type 11 body is still [unk] but its effect is `{code, flags}` handed to `SendInput`), rotation (type 9) and EVT_DISPLAY_SETTINGS (type 6) payloads (need a phone capture), numeric codes inside the service's own settings reload, and the service-side use of the `Server` registry key.
