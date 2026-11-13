# Design: módulo de vídeo do MonRem Host

Data: 2026-11-13. Fonte de requisitos: `SPEC.md` (protocolo, console, bandeja, origem dos quadros). Estado e checklist: `HANDOFF.md`.
Marcação: **[decisão]** = decidido com o dono; **[minha escolha]** = escolha minha, para revisar.

## 1. Intenção
Um monitor virtual do Windows que aparece como segunda tela nos tablets e PCs, usando o transporte do SPEC (TCP 28252, TLS 1.3 PSK, controle de fluxo por ACK) para que os apps spacedesk existentes funcionem como visualizadores. Roda dentro do `MonRem.Host`, junto com o controle do MSFS. Configurações, status, diagnóstico e bandeja (SPEC 16 e 17) entram no host e no app do MonRem, não num programa separado.

Por que: ter o monitor virtual e o controle do rig no mesmo produto, sem depender do spacedesk instalado e sem a data de validade dele (31/12/2027, SPEC 19).

## 2. Objetivos, não-objetivos, anti-objetivos
**Objetivos**
- Os apps spacedesk (Android no celular já verificado, Windows) conectam, mostram o monitor e enviam mouse, toque, teclado e rotação.
- Pipeline testável sem driver e sem GPU específica.
- Trocar a origem dos quadros sem mexer no resto.

**Não-objetivos**
- VideoWall, áudio, USB (AOAP e usbmuxd), cliente WebSocket, licenciamento.
- Compatibilidade com o serviço do fornecedor (pipe da bandeja, chaves de registro): só interessa onde o app spacedesk exige.

**Anti-objetivos (seria fracasso mesmo funcionando)**
- Copiar código decompilado (`research/vendor-reference/`).
- Reintroduzir o design antigo de vídeo do MonRem (SudoVDA, spikes, mídia UDP).
- Produto que só funciona com o spacedesk instalado.

## 3. Restrições
- `MonRem.Host` é um serviço do Windows (sessão 0) em `net10.0-windows`; `Host.Core` é multiplataforma e o CI roda em Ubuntu e Windows. Partes testáveis no Linux devem ficar sem API do Windows.
- Serviço na sessão 0 não captura o desktop do usuário nem injeta teclado: isso exige um processo na sessão do usuário (SPEC 17.3).
- O app spacedesk exige: quadros do tamanho identificado (nunca trocar largura e altura, SPEC 15.10), um access unit H.264 Annex B por mensagem, AUD+SPS+PPS+IDR em keyframes, ACK com janela 2.
- Driver exige assinatura; para uso pessoal, modo de teste ou certificado autoassinado bastam [não verificado aqui].

## 4. Abordagens consideradas
| | Descrição | Prós | Contras |
| --- | --- | --- | --- |
| A | Só o driver do spacedesk instalado | Sem driver para escrever | Licença não lida, expira em 2027, contrato de IOCTL incompleto |
| B | Só driver IddCx próprio | Independente, sem validade | Nada testável com monitor virtual até o driver existir |
| **C (escolhida)** | `IFrameSource` com três origens: sintética/captura real, adaptador do spacedesk (só dev), driver próprio | Pipeline testável já; sem ficar parado; destino final continua B | Mais uma camada; adaptador depende de fechar o contrato |

**[decisão]** C, com B como destino final e o adaptador do spacedesk marcado dev-only, fora do build de release.

## 5. Arquitetura

### 5.1 Projetos
| Projeto | Conteúdo | Alvo |
| --- | --- | --- |
| `src/host/MonRem.Host.Video` | Protocolo de fios (cabeçalho de 128 bytes, tipos), servidor TCP/TLS-PSK, sessão por cliente, controle de fluxo, descoberta UDP, interfaces `IFrameSource`, `IVideoEncoder`, `IInputSink`, `IDisplayRotator` | multiplataforma |
| `src/host/MonRem.Host.Video.Windows` | Encoder Media Foundation, origem Desktop Duplication, adaptador do spacedesk, injeção de entrada, rotação (CCD), cliente do pipe do helper | `net10.0-windows` |
| `src/host/MonRem.Host.Video.Driver` (futuro) | Origem do driver IddCx próprio | `net10.0-windows` |
| `src/host/MonRem.Host.Video.Helper` | Processo da sessão do usuário: bandeja, `SendInput`, `SetDisplayConfig`, captura de monitor real | `net10.0-windows` |
| `drivers/monrem-iddcx` (futuro) | Driver UMDF IddCx em C++ | C++ |
| testes | `tests/MonRem.Tests` (protocolo, sessão, fluxo, descoberta, contra o `ref_server` como oráculo) | multiplataforma |

O host só referencia `Host.Video.Windows` no Windows, como faz com `Host.Msfs`.

### 5.2 Interfaces centrais
- `IFrameSource`: `Plug(resolutions, edid)`, `Unplug`, `WaitForFrame(ct)` devolvendo `Frame` (textura ou buffer BGRA, largura, altura, regiões sujas), `Cursor`, `RenderAdapterLuid`.
- `IVideoEncoder`: `Encode(frame, forceKeyframe)` devolvendo um access unit Annex B.
- `IInputSink`: mouse relativo, toque, teclado (Unicode, scan code Android/iOS, hardware), com mapa de rotação.

### 5.3 Pipeline por cliente
```
IFrameSource -> fila limitada (1) -> encoder -> remetente TCP (janela ACK 2)
```
Descarta quadro antigo quando a rede está lenta, nunca enfileira. Keyframe sob demanda e a cada GOP curto [minha escolha]: perfil Main, nível 4.2, baixa latência. H.264 por hardware primeiro, software de fallback.

### 5.4 Origens, em ordem de construção
1. **Sintética**: padrão de teste (barra móvel, marcador de canto), e **captura de monitor real** por Desktop Duplication. Rodam no helper ou no host em modo console, na sessão do usuário. Não criam monitor: servem para pôr o pipeline de pé.
2. **Adaptador do spacedesk** (dev-only): IOCTLs da seção 19 do SPEC. Exige parar o serviço do fornecedor. Só entra se a análise da DLL do driver ou um teste ao vivo fechar o formato do plug, das regiões e do cursor.
3. **Driver IddCx próprio**: mesmo contrato da seção 19, definido por nós. Assinatura para uso pessoal [ver 3].

### 5.5 Transporte e descoberta
Conforme SPEC 12 a 15: identificação (334 bytes), preâmbulo (settings, visibility x2, ping x2), quadros tipo 2, ACK tipo 7, ping de 15 s. Tolerar tipo 6 antes da identificação (SPEC 15.6). TLS 1.3 PSK com `TLS_CHACHA20_POLY1305_SHA256` e `psk_dhe_ke`, PSK = `hex(EncryptionPassword + "1A"*16)` (SPEC 15.9): .NET não expõe PSK externo no `SslStream`, então o TLS fica na camada do servidor [**decisão em aberto para o construtor**, ver 8]. Descoberta UDP 28252: prefixo `SPACEDESK-NET-CLIENT\0`, resposta de 308 bytes (SPEC 13.1), com o bit de TLS no byte 280.

### 5.6 Entrada e rotação
- Mouse (tipo 10) e toque (tipo 12) pelo `IInputSink`; teclado (tipo 11) em três caminhos (SPEC 15.11) com `keymap.csv`. Teclado precisa do helper (`SendInput`).
- Rotação (tipo 9): valor = `DISPLAYCONFIG_ROTATION`; **o tamanho do quadro não muda**; o Windows gira o desktop dentro do quadro e a mesma transformada vale para toque e mouse absoluto (SPEC 15.10).

### 5.7 Helper e IPC
Processo da sessão do usuário, iniciado com o login. Pipe nomeado entre host e helper com **ACL explícita** (SYSTEM e o usuário interativo), cabeçalho de 8 bytes `{id, tamanho}` como no SPEC 17.1, mas com mensagens próprias do MonRem [minha escolha]. O helper cuida de bandeja, balões, `SendInput`, rotação e leitura de coordenadas dos monitores.

### 5.8 Configuração, status, diagnóstico
- Configuração em `MonRem:Video:*` (opções do host), com recarga a quente (SPEC 16.6c): reinicia só o listener quando LAN, criptografia ou senha mudam.
- Status por cliente (SPEC 16.4) exposto ao app do MonRem pelo canal de controle existente; diagnóstico e solução de problemas conforme SPEC 16.5 e 16.6, adaptados (sem DebugView, sem registro do fornecedor).
- A senha de criptografia não fica em texto puro no registro como no fornecedor [minha escolha: guardar em `DataDirectory`, com ACL].

## 6. Testes
- Protocolo, sessão e controle de fluxo em xUnit, sem driver e sem GPU, usando a origem sintética.
- `tools/video-oracle/ref_server` como oráculo de comportamento; teste de interoperabilidade C# contra ele.
- Manual: celular Android e cliente Windows (SPEC 14 e 15).
- Encoder e captura: testes só no Windows; o CI Windows os roda.

## 7. Ordem de trabalho (insumo do plano)
1. `Host.Video`: protocolo, framing, servidor TCP, identificação e preâmbulo, descoberta UDP, origem sintética, encoder de teste (I_PCM como o `ref_server`). Meta: celular conecta e vê o padrão.
2. Encoder Media Foundation (H.264 real) e controle de fluxo/descartes.
3. Entrada: mouse, toque, depois helper e teclado.
4. TLS-PSK.
5. Rotação.
6. Configuração, status, diagnóstico, bandeja.
7. Origem de captura de monitor real.
8. Análise da DLL do driver e decisão sobre o adaptador do spacedesk.
9. Driver IddCx próprio.

## 8. Deixado para o construtor
- **TLS 1.3 com PSK externo** em .NET (provavelmente exige biblioteca ou implementação própria; avaliar BouncyCastle e SChannel antes de escolher). Impacto: etapa 4.
- Formato dos registros de região e do cursor (SPEC 19), caso o adaptador do spacedesk seja feito.
- GOP, taxa de bits e perfil exatos: afinar com o celular.
- Mensagens do pipe host-helper: definir no plano.
- Valores atuais e processo de assinatura do driver: verificar antes da etapa 9.
