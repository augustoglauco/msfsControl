// Monitor Remoto — cabeçalho binário do canal de mídia (UDP)
// Datagrama: [MediaHeader 28 bytes][payload cifrado][tag GCM 16 bytes], total <= 1400 bytes.
// Little-endian. O cabeçalho inteiro é AAD do AES-128-GCM.
// Chave e salt derivados por (stream_id, direção, fase) do segredo da sessão S (ver control.proto).
// Nonce (12 bytes) = salt(4) ‖ u64 big-endian(seq). stream_id nunca é reaproveitado na sessão.
// Anti-replay: janela de 64 pacotes por stream. Troca de chave: alterna MF_KEY_PHASE.
// Wrap: capture_ts_us volta a zero a cada ~71 min; comparar com aritmética modular.
// Maior frame suportado: 4 blocos FEC × (255 − paridade) shards × 1340 B ≈ 1,3 MB.
#pragma once
#include <stdint.h>

#define MONREM_MEDIA_VERSION      1
#define MONREM_MEDIA_MAX_DATAGRAM 1400
#define MONREM_MEDIA_HEADER_SIZE  28
#define MONREM_GCM_TAG_SIZE       16
#define MONREM_MEDIA_SHARD_SIZE   1340 /* 28 + 1340 + 16 = 1384 <= 1400 */
#define MONREM_STREAM_ID_MIN      1
#define MONREM_STREAM_ID_MAX      254

enum MonRemMediaType {
  MONREM_MT_VIDEO_DATA   = 1,
  MONREM_MT_VIDEO_PARITY = 2,
  /* 3 reservado (forma do cursor vai no canal de controle) */
  MONREM_MT_CURSOR_POS   = 4,  /* payload: CursorPos   */
  /* 5 reservado (áudio, v2) */
  MONREM_MT_CLIENT_HELLO = 6,  /* cliente → host, stream_id = 0, payload: UdpHello (input.proto) */
};

enum MonRemMediaFlags {
  MONREM_MF_SOF         = 1u << 0,  /* primeiro shard do frame          */
  MONREM_MF_EOF         = 1u << 1,  /* último shard de dados do frame   */
  MONREM_MF_KEYFRAME    = 1u << 2,  /* IDR                              */
  MONREM_MF_RECOVERY    = 1u << 3,  /* primeiro frame após RFI          */
  MONREM_MF_DISCARDABLE = 1u << 4,  /* pode ser descartado sem RFI/IDR  */
  MONREM_MF_KEY_PHASE   = 1u << 5,  /* fase da chave em uso (alterna na troca) */
};

#pragma pack(push, 1)
typedef struct MonRemMediaHeader {
  uint8_t  ver_type;         /* (version << 4) | type */
  uint8_t  stream_id;
  uint16_t flags;
  uint32_t seq;              /* por stream e fase de chave; nunca repete */
  uint32_t frame_id;         /* por stream */
  uint16_t shard_idx;        /* dentro do bloco FEC */
  uint8_t  data_shards;
  uint8_t  parity_shards;    /* >= 2 */
  uint8_t  fec_block_idx;
  uint8_t  fec_block_count;  /* 1..4 */
  uint16_t reserved;         /* 0 */
  uint32_t frame_size;       /* bytes do frame codificado inteiro */
  uint32_t capture_ts_us;    /* 32 bits baixos do relógio monotônico do host */
} MonRemMediaHeader;

typedef struct MonRemCursorPos {
  uint64_t shape_hash;
  uint16_t x, y;             /* normalizado 0..65535 no monitor */
  uint8_t  visible;
  uint8_t  reserved[3];
} MonRemCursorPos;
#pragma pack(pop)

_Static_assert(sizeof(MonRemMediaHeader) == MONREM_MEDIA_HEADER_SIZE, "header deve ter 28 bytes");
