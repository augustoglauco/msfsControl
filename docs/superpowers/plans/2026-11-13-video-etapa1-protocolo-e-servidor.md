# Vídeo, etapa 1: protocolo e servidor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** O celular com o app spacedesk descobre o MonRem Host, conecta sem senha e mostra um padrão de teste em movimento, com o ACK de fluxo funcionando.

**Architecture:** Novo projeto multiplataforma `MonRem.Host.Video` com o protocolo de fios do SPEC, um servidor TCP por cliente com janela de ACK, um respondedor de descoberta UDP, e as interfaces `IFrameSource` e `IVideoEncoder` com implementações sintéticas (barra móvel e H.264 só com coeficientes DC, portado do oráculo Python). O host registra o módulo como serviço hospedado, desligado por padrão.

**Tech Stack:** C# net10.0, sockets do BCL, xUnit 2.9.3. Nenhum pacote NuGet novo.

**Spec:** `docs/video/DESIGN.md` (seções 5.1 a 5.5, 7 etapa 1) e `docs/video/SPEC.md` (seções 12 a 15). Oráculo de valores: `tools/video-oracle/ref_server/{proto.py,server.py,h264_pcm.py}`.

## Global Constraints

- `net10.0`, `Nullable` ligado, sem aviso em CI (`TreatWarningsAsErrors` com `CI=true`). Sem pacote novo em `Directory.Packages.props`.
- Projeto `src/host/MonRem.Host.Video` espelha o `Host.Core`: `Microsoft.NET.Sdk`, `FrameworkReference Microsoft.AspNetCore.App`, `InternalsVisibleTo MonRem.Tests`. Sem API do Windows. Entra na `MonRem.sln`.
- Mensagem = cabeçalho de **128 bytes** (32 `uint32` little-endian; int 0 = tipo, int 1 = bytes de corpo) + corpo. Sem outro enquadramento.
- Tipos: 0 Identification, 1 Ping, 2 Framebuffer, 3 Visibility, 4 DisplaySettings, 6 EvtDisplaySettings, 7 FlowControlAck, 8 Error, 9 Rotation, 10 Mouse, 11 Keyboard, 12 Touch, 13 Pen, 14 AudioSpeaker.
- Portas padrão: TCP 28252 e UDP 28252.
- Descoberta: pedido começa com `SPACEDESK-NET-CLIENT\0` (21 bytes; o cliente Windows manda o mesmo prefixo preenchido até 1024 bytes). Resposta unicast de **308 bytes**: bytes 0 a 259 = nome do host em UTF-16LE com zeros; offset 260 = `uint32` porta TCP; offset 264 = 10; offsets 272, 276, 280 = 4, 8, 43. **Byte 280 sem o bit 0x10** (sem TLS).
- Identificação (cliente → servidor, corpo de **334** bytes): int 8 = fps, int 13 = largura, int 22 = altura; corpo UTF-16LE: GUID do aparelho terminado em NUL no offset 0, nome do aparelho no offset 78.
- Preâmbulo do servidor, nesta ordem: DisplaySettings (tudo zero), Visibility (int 3 = 1), Visibility (int 2 = 1, int 3 = 1), Ping, Ping (tudo zero).
- Cabeçalho de Framebuffer, valores fixos: i0=2, i1=tamanho, i2=largura, i3=altura, i4=largura*4, i5=21, i8=largura, i9=altura, i10=4, i11=2, i12=fps, i13=4, i15=1, i17=tamanho. Um access unit H.264 Annex B por mensagem, com AUD (NAL 9) no início; em keyframe: AUD, SPS (7), PPS (8), IDR (5), start codes de 4 bytes.
- FlowControlAck: cabeçalho só com i0=7, sem corpo, um por quadro. Janela de **2** quadros sem ACK. Quadro desatualizado é descartado, nunca enfileirado.
- O tamanho do quadro é o identificado e nunca muda durante a sessão.
- Sem TLS e sem senha. Aceitar mensagem tipo 6 antes da identificação (SPEC 15.6).
- Esta etapa aceita qualquer aparelho. Por isso `Video:Enabled` é falso por padrão. A aprovação por dispositivo (DESIGN 5.9) é a etapa 4.

## Review Focus

- Cabeçalho com corpo gigante (i1 = `0xFFFFFFFF`): fechar a conexão sem alocar o corpo.
- Cliente que conecta e nunca se identifica: fechar após 10 s.
- Cliente que para de dar ACK ou some no meio de um quadro: sessão termina em 45 s e não trava outros clientes.
- Identificação com tamanho 0x0 ou absurdo (acima de 8192 em qualquer eixo): usar 1280x720.
- Dois clientes ao mesmo tempo, um cai: o outro segue sem interrupção.
- Datagrama de descoberta sem o prefixo: nenhuma resposta.

---

### Task 1: Projeto e codec do protocolo

**Files:**
- Create: `src/host/MonRem.Host.Video/MonRem.Host.Video.csproj`, `src/host/MonRem.Host.Video/Wire/MessageType.cs`, `src/host/MonRem.Host.Video/Wire/WireMessage.cs`, `src/host/MonRem.Host.Video/Wire/WireCodec.cs`
- Modify: `MonRem.sln` (adicionar o projeto), `tests/MonRem.Tests/MonRem.Tests.csproj` (ProjectReference para `..\..\src\host\MonRem.Host.Video\MonRem.Host.Video.csproj`)
- Test: `tests/MonRem.Tests/Video/WireCodecTests.cs`

**Interfaces:**
- Produces:
  - `enum MessageType : uint` com os valores das Global Constraints (`Identification`, `Ping`, `Framebuffer`, `Visibility`, `DisplaySettings`, `EvtDisplaySettings`, `FlowControlAck`, `Error`, `Rotation`, `InputMouse`, `InputKeyboard`, `InputTouch`, `InputPen`, `AudioSpeaker`).
  - `readonly record struct WireMessage(uint[] Ints, byte[] Body)` com `MessageType Type => (MessageType)Ints[0]` e `static WireMessage Create(MessageType type, ReadOnlySpan<byte> body = default, params (int Index, uint Value)[] ints)`. `Ints` sempre tem 32 posições; o construtor de `Create` grava i0 e i1.
  - `static class WireCodec` com `const int HeaderSize = 128`, `ValueTask WriteAsync(Stream s, WireMessage m, CancellationToken ct)` (cabeçalho e corpo numa só escrita) e `ValueTask<WireMessage?> ReadAsync(Stream s, int maxBody, CancellationToken ct)`: devolve `null` se o fluxo termina limpo exatamente na fronteira de uma mensagem, lança `InvalidDataException` se i1 > `maxBody`, lança `EndOfStreamException` se termina no meio.

- [ ] **Step 1: Testes que falham** em `WireCodecTests`: `Roundtrip_preserves_ints_and_body` (escreve num `MemoryStream`, relê, compara i0, i1, i13 e o corpo); `Header_is_128_bytes_little_endian` (mensagem Ping: `stream.Length == 128` e os 4 primeiros bytes são `01 00 00 00`); `Read_returns_null_on_clean_eof`; `Read_rejects_body_larger_than_max` (i1 = 0xFFFFFFFF com `maxBody` 4096 lança `InvalidDataException` sem ler corpo); `Read_throws_on_truncated_body`.
- [ ] **Step 2: Rodar e ver falhar:** `dotnet test MonRem.sln --filter FullyQualifiedName~WireCodecTests`. Esperado: erro de compilação (tipos inexistentes).
- [ ] **Step 3: Criar o csproj** espelhando o `Host.Core` (Global Constraints), adicionar à `.sln` com `dotnet sln MonRem.sln add`, e implementar `MessageType`, `WireMessage` e `WireCodec` com as assinaturas acima.
- [ ] **Step 4: Rodar:** mesmo comando. Esperado: 5 testes passando.
- [ ] **Step 5: Commit:** `git add -A src/host/MonRem.Host.Video tests MonRem.sln && git commit -m "Video: projeto Host.Video e codec do protocolo de fios"`

### Task 2: Construtores e leitor de mensagens

**Files:**
- Create: `src/host/MonRem.Host.Video/Wire/Messages.cs`, `src/host/MonRem.Host.Video/Wire/Identification.cs`
- Test: `tests/MonRem.Tests/Video/MessagesTests.cs`

**Interfaces:**
- Consumes: `WireMessage`, `MessageType` (Task 1).
- Produces:
  - `static class Messages` com `WireMessage Settings()`, `Visibility(uint int2, uint int3)`, `Ping()`, `Ack()`, `Framebuffer(int width, int height, int fps, ReadOnlyMemory<byte> accessUnit)` (monta os ints das Global Constraints) e `IReadOnlyList<WireMessage> Preamble()` (as cinco mensagens, na ordem).
  - `sealed record Identification(int Width, int Height, int Fps, string DeviceId, string DeviceName)` com `static bool TryParse(WireMessage m, out Identification? result)`: exige `Type == Identification` e corpo de 334 bytes; largura i13, altura i22, fps i8; GUID e nome conforme as Global Constraints.
  - `static Identification.Create(int width, int height, int fps, string deviceId, string deviceName)` devolvendo a `WireMessage` (para os testes e o futuro viewer). Valores dos demais ints de uma identificação: copiar de `proto.py::identification` (i2=4, i3=8, i5=3, i6=3, i7=2, i9=0x00010005, i11=0x0004003C, i12=1, i31=1).

- [ ] **Step 1: Testes que falham:** `Framebuffer_header_matches_oracle` (1560x720@60, corpo de 100 bytes: i0=2, i1=100, i2=1560, i3=720, i4=6240, i5=21, i8=1560, i9=720, i10=4, i11=2, i12=60, i13=4, i15=1, i17=100); `Preamble_order` (tipos 4, 3, 3, 1, 1 e os ints de cada Visibility); `Ack_is_header_only` (i0=7, i1=0); `Identification_roundtrip` (cria com 1560x720@60, GUID `{AAAAAAAA-0000-0000-0000-000000000001}` e nome `SM-S928B`, faz `TryParse`, compara tudo); `TryParse_rejects_wrong_body_size` e `TryParse_rejects_wrong_type`.
- [ ] **Step 2: Rodar:** `dotnet test MonRem.sln --filter FullyQualifiedName~MessagesTests`. Esperado: falha de compilação.
- [ ] **Step 3: Implementar** `Messages` e `Identification` com as assinaturas acima. Texto UTF-16LE: use `Encoding.Unicode`.
- [ ] **Step 4: Rodar** o mesmo filtro. Esperado: 6 passando.
- [ ] **Step 5: Commit:** `git commit -am "Video: construtores de mensagem e leitura da identificacao"` (com `git add` dos arquivos novos antes).

### Task 3: Descoberta UDP

**Files:**
- Create: `src/host/MonRem.Host.Video/Discovery/DiscoveryReply.cs`, `src/host/MonRem.Host.Video/Discovery/DiscoveryResponder.cs`
- Test: `tests/MonRem.Tests/Video/VideoDiscoveryTests.cs`

**Interfaces:**
- Produces:
  - `static class DiscoveryReply` com `const int Size = 308`, `static readonly byte[] RequestPrefix` (os 21 bytes) e `static byte[] Build(string hostName, int tcpPort)` (layout das Global Constraints; nome truncado em 258 bytes).
  - `sealed class DiscoveryResponder : IAsyncDisposable` com construtor `(IPAddress bindAddress, int udpPort, string hostName, int tcpPort, ILogger? logger = null)`, `int LocalPort { get; }` (porta efetiva quando `udpPort` = 0), `void Start()` e `DisposeAsync()`. Responde unicast ao remetente para cada datagrama que **comece** com `RequestPrefix`, de qualquer tamanho; ignora o resto; se o socket falhar, recria após 3 s.

- [ ] **Step 1: Testes que falham:** `Reply_layout` (`Build("Flight1", 28252)`: tamanho 308; nome UTF-16LE no início; `BitConverter.ToUInt32(r, 260) == 28252`; ints em 264, 272, 276 e `r[280] == 43` e `(r[280] & 0x10) == 0`); `Responder_answers_prefix_request` (responder em `127.0.0.1` porta 0; cliente UDP envia o prefixo de 21 bytes e recebe 308 bytes); `Responder_answers_1024_byte_padded_request`; `Responder_ignores_other_datagrams` (envia `HELLO`, espera 300 ms, nenhuma resposta).
- [ ] **Step 2: Rodar:** filtro `VideoDiscoveryTests`. Esperado: falha de compilação.
- [ ] **Step 3: Implementar** com `UdpClient` e um laço de recebimento cancelável. Broadcast é recebido automaticamente quando o bind é `IPAddress.Any`.
- [ ] **Step 4: Rodar.** Esperado: 4 passando.
- [ ] **Step 5: Commit:** `git commit -m "Video: respondedor de descoberta UDP"`.

### Task 4: Fonte sintética e codificador de teste

**Files:**
- Create: `src/host/MonRem.Host.Video/Pipeline/Frame.cs`, `IFrameSource.cs`, `IVideoEncoder.cs`, `src/host/MonRem.Host.Video/Pipeline/SyntheticFrameSource.cs`, `src/host/MonRem.Host.Video/Pipeline/TestPatternEncoder.cs`
- Test: `tests/MonRem.Tests/Video/TestPatternEncoderTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct Frame(int Width, int Height, int Stride, ReadOnlyMemory<byte> Bgra)`.
  - `interface IFrameSource : IAsyncDisposable { int Width { get; } int Height { get; } ValueTask<Frame?> WaitForFrameAsync(CancellationToken ct); }` (devolve `null` no fim; honra cancelamento).
  - `interface IVideoEncoder : IDisposable { ReadOnlyMemory<byte> Encode(in Frame frame, bool forceKeyframe); }`
  - `sealed class SyntheticFrameSource(int width, int height, int fps) : IFrameSource`: um quadro BGRA por `1/fps` s (usar `TimeProvider`/`PeriodicTimer`), fundo escuro com uma **barra vertical clara** de 5% da largura que avança da esquerda para a direita em 4 s e recomeça; um marcador claro de 16x16 no canto superior esquerdo.
  - `sealed class TestPatternEncoder : IVideoEncoder`: cada `Encode` devolve um access unit **IDR completo** (AUD, SPS, PPS, IDR), perfil Main (profile_idc 77), nível 4.2 (level_idc 42), macroblocos I16x16 só com coeficiente DC, QP 51, deblocking desligado. Luma de cada macroblock = média do luma BGRA da região 16x16; croma neutro. **Portar** de `tools/video-oracle/ref_server/h264_pcm.py` as rotinas `sps`, `pps` e `idr_pattern` (escritor de bits Exp-Golomb, `_nal` com emulation prevention). Os SPS para 1560x720 começam com os bytes `67 4D 40 2A`.

- [ ] **Step 1: Testes que falham:** `Encoder_emits_aud_sps_pps_idr_in_order` (para 1560x720 e 64x64: separar NALs por start code de 4 bytes e conferir tipos 9, 7, 8, 5 nessa ordem); `Sps_header_bytes_for_1560x720` (`67 4D 40 2A`); `Encoder_output_is_small` (menos de 64 KiB para 1560x720); `Synthetic_source_bar_moves` (dois quadros com 1 s de diferença usando um `TimeProvider` manual ou fps alto: a coluna da barra muda); `Source_stops_on_cancellation`.
- [ ] **Step 2: Rodar:** filtro `TestPatternEncoderTests`. Esperado: falha de compilação.
- [ ] **Step 3: Implementar** as interfaces e as duas classes. A decodificação real não é testável aqui: a prova é a verificação manual da Task 6 com o celular.
- [ ] **Step 4: Rodar.** Esperado: 5 passando.
- [ ] **Step 5: Commit:** `git commit -m "Video: IFrameSource, IVideoEncoder, fonte sintetica e codificador de teste"`.

### Task 5: Servidor e sessão por cliente

**Files:**
- Create: `src/host/MonRem.Host.Video/Server/VideoServerOptions.cs`, `src/host/MonRem.Host.Video/Server/VideoServer.cs`, `src/host/MonRem.Host.Video/Server/ClientSession.cs`
- Test: `tests/MonRem.Tests/Video/VideoServerTests.cs`

**Interfaces:**
- Consumes: `WireCodec`, `Messages`, `Identification` (Tasks 1 e 2); `DiscoveryResponder` (Task 3); `IFrameSource`, `IVideoEncoder` (Task 4).
- Produces:
  - `sealed class VideoServerOptions { string HostName; int TcpPort = 28252; int UdpPort = 28252; int Fps = 30; int AckWindow = 2; TimeSpan IdentifyTimeout = 10 s; TimeSpan ConnectionLossTimeout = 45 s; int MaxDimension = 8192; }`.
  - `delegate IFrameSource FrameSourceFactory(int width, int height, int fps)`.
  - `sealed class VideoServer : IAsyncDisposable` com construtor `(VideoServerOptions options, FrameSourceFactory sources, Func<IVideoEncoder> encoders, ILogger? logger = null)`, `int LocalTcpPort`, `int LocalUdpPort`, `Task RunAsync(CancellationToken ct)` (sobe listener TCP em `IPAddress.Any` e o `DiscoveryResponder`; uma `ClientSession` por conexão; retorna ao cancelar) e `int ActiveSessions`.
  - `ClientSession` (interna): lê a identificação com `IdentifyTimeout` (descarta mensagens tipo 6 antes dela; qualquer outra coisa ou corpo ≠ 334 fecha); dimensões 0 ou acima de `MaxDimension` viram 1280x720, fps 0 vira `options.Fps`; envia o preâmbulo; cria fonte e codificador; laço de quadros com semáforo de crédito = `AckWindow`: espera crédito, pega o quadro mais recente da fonte, codifica (keyframe sempre, o codificador de teste só faz IDR), envia `Messages.Framebuffer`; leitor em paralelo: ACK devolve crédito, PING e mensagens de entrada (9 a 13) e as desconhecidas são lidas e registradas no log e ignoradas, `ReadAsync` com `maxBody` 4096; se nada chega por `ConnectionLossTimeout`, encerra. Sessões terminam sem derrubar o servidor nem outras sessões.

- [ ] **Step 1: Testes que falham** (cliente TCP de teste em `127.0.0.1` na porta efetiva, `VideoServerOptions` com `TcpPort = 0`, `UdpPort = 0`, fonte e codificador sintéticos): `Handshake_sends_preamble_then_frames` (identifica 320x240@30; lê os 5 do preâmbulo na ordem; recebe 3 quadros com i2=320, i3=240; o corpo começa com start code, AUD); `Stalls_after_window_without_ack` (sem ACK chegam exatamente 2 quadros; após 1 ACK chega o terceiro); `Accepts_type6_before_identification`; `Rejects_oversized_header_without_allocating` (i1 = 0xFFFFFFFF: servidor fecha a conexão e continua aceitando outras); `Identification_timeout_closes` (`IdentifyTimeout` = 200 ms); `Zero_size_identification_falls_back_to_1280x720`; `Two_clients_independent` (dois clientes, fecha o primeiro, o segundo continua recebendo quadros); `Connection_loss_ends_session` (`ConnectionLossTimeout` = 300 ms, cliente quieto, `ActiveSessions` volta a 0).
- [ ] **Step 2: Rodar:** filtro `VideoServerTests`. Esperado: falha de compilação.
- [ ] **Step 3: Implementar** `VideoServerOptions`, `VideoServer` e `ClientSession`. Um `Channel`/semáforo por sessão; nenhuma fila de quadros além do quadro mais recente.
- [ ] **Step 4: Rodar.** Esperado: 8 passando, sem testes pendurados (a suíte inteira roda em poucos segundos).
- [ ] **Step 5: Commit:** `git commit -m "Video: servidor TCP com sessao por cliente e controle de fluxo por ACK"`.

### Task 6: Integração no host e verificação com o celular

**Files:**
- Create: `src/host/MonRem.Host.Video/VideoOptions.cs`, `src/host/MonRem.Host.Video/VideoHostedService.cs`, `src/host/MonRem.Host.Video/VideoServiceCollectionExtensions.cs`
- Modify: `src/host/MonRem.Host/MonRem.Host.csproj` (ProjectReference), `src/host/MonRem.Host/Program.cs` (registro), `src/host/MonRem.Host/appsettings.json`, `docs/guias/desenvolvimento.md` (comando e aviso de firewall)
- Test: `tests/MonRem.Tests/Video/VideoOptionsTests.cs`

**Interfaces:**
- Consumes: `VideoServer`, `VideoServerOptions`, `SyntheticFrameSource`, `TestPatternEncoder` (Tasks 4 e 5).
- Produces: `sealed class VideoOptions { const string Section = "MonRem:Video"; bool Enabled = false; int TcpPort = 28252; int UdpPort = 28252; int Fps = 30; string? HostName; }`; `IServiceCollection AddMonRemVideo(this IServiceCollection services, IConfiguration configuration)` registra o `VideoOptions` e, só se `Enabled`, um `BackgroundService` que cria o `VideoServer` com a fonte e o codificador sintéticos (`HostName` padrão = `MonRemHostOptions.HostName`).

- [ ] **Step 1: Testes que falham:** `Options_default_to_disabled_on_28252` e `Disabled_registers_no_hosted_service` (monta um `ServiceCollection` com configuração vazia, chama `AddMonRemVideo`, nenhum `IHostedService` de vídeo registrado) e `Enabled_binds_from_configuration` (`MonRem:Video:Enabled=true`, `TcpPort=0`, `UdpPort=0`).
- [ ] **Step 2: Rodar:** filtro `VideoOptionsTests`. Esperado: falha de compilação.
- [ ] **Step 3: Implementar** as três classes e ligar no `Program.cs` com `builder.Services.AddMonRemVideo(builder.Configuration)`. Em `appsettings.json` adicionar `"Video": { "Enabled": false }` dentro de `MonRem`. Em `desenvolvimento.md`: como ligar (`--MonRem:Video:Enabled=true`), que o serviço do spacedesk precisa estar parado (mesma porta 28252), e a regra de firewall para TCP e UDP 28252.
- [ ] **Step 4: Rodar a suíte inteira:** `dotnet test MonRem.sln`. Esperado: tudo passando (111 testes anteriores mais os novos). Rodar também `dotnet build src/host/MonRem.Host -c Release`. Esperado: sem erro nem aviso.
- [ ] **Step 5: Verificação manual com o celular** (registrar o resultado no commit): parar o serviço (`Stop-Service spacedeskService`), rodar `dotnet run --project src/host/MonRem.Host -- --MonRem:UseFakeSim=true --MonRem:Video:Enabled=true`, abrir o app spacedesk no Android e escolher o host na lista. Esperado: o host aparece na lista, a conexão fica estável, uma barra clara se move da esquerda para a direita sobre fundo escuro, o log do host mostra a identificação 1560x720@60 (ou o tamanho do aparelho) e os quadros sendo confirmados. Religar o serviço depois (`Start-Service spacedeskService`).
- [ ] **Step 6: Commit:** `git commit -m "Video: modulo ligado ao host, desligado por padrao; verificado com o celular"`.

---

## Self-review

- **Cobertura do DESIGN (etapa 1):** protocolo e framing (T1, T2), servidor TCP, identificação e preâmbulo (T5), descoberta UDP (T3), origem sintética e codificador de teste (T4), meta "celular conecta e vê o padrão" (T6 passo 5). Fora desta etapa, de propósito: encoder H.264 real e descarte de quadros por rede lenta com quadro mais recente além do básico (etapa 2), entrada (3), aprovação de dispositivos (4), rotação (5).
- **Consistência de tipos:** `WireMessage`, `MessageType`, `WireCodec`, `Messages`, `Identification`, `Frame`, `IFrameSource`, `IVideoEncoder`, `FrameSourceFactory`, `VideoServerOptions` usados com os mesmos nomes entre as tarefas.
- **Proporção:** seis tarefas, só assinaturas, nomes de testes e valores fixos do SPEC; o único trecho que exige leitura do oráculo é o escritor H.264 da Task 4.
- **Lacunas conhecidas:** o critério final (aparecer no celular) depende de teste manual; o codificador de teste só gera IDR, como o `ref_server` já validou com o aparelho (SPEC 14).
