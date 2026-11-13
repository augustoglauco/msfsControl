# Design: módulo de vídeo do MonRem Host

Data: 2026-11-13. Fonte de requisitos: `SPEC.md` (protocolo, console, bandeja, origem dos quadros). Estado e checklist: `HANDOFF.md`.
Status: **aprovado pelo dono em 2026-11-13** (incluindo aprovação única por dispositivo, 5.9, e o viewer próprio, 5.10).
Marcação: **[decisão]** = decidido com o dono; **[minha escolha]** = escolha minha, para revisar.

## 1. Intenção
Um monitor virtual do Windows que aparece como segunda tela nos tablets e PCs, usando o transporte do SPEC (TCP 28252, controle de fluxo por ACK; **sem senha**, ver 5.9) para que os apps spacedesk existentes funcionem como visualizadores. Roda dentro do `MonRem.Host`, junto com o controle do MSFS. Configurações, status, diagnóstico e bandeja (SPEC 16 e 17) entram no host e no app do MonRem, não num programa separado.

Por que: ter o monitor virtual e o controle do rig no mesmo produto, sem depender do spacedesk instalado e sem a data de validade dele (31/12/2027, SPEC 19).

## 2. Objetivos, não-objetivos, anti-objetivos
**Objetivos**
- Os apps spacedesk (Android no celular já verificado, Windows) conectam **sem digitar senha**, mostram o monitor e enviam mouse e toque; o teclado do cliente Windows também funciona sem senha (SPEC 15.7).
- Pipeline testável sem driver e sem GPU específica.
- **Viewer próprio do MonRem** (fase tardia, 5.10) que fala o mesmo protocolo e entrega teclado e rotação no celular sem senha.
- Trocar a origem dos quadros sem mexer no resto.

**Não-objetivos**
- VideoWall, áudio, USB (AOAP e usbmuxd), cliente WebSocket, licenciamento.
- **Senhas e TLS-PSK.** O app spacedesk só pede senha para ligar a criptografia, e é nesse modo que o app Android envia teclado e rotação (SPEC 15.9). Abrir mão da senha significa abrir mão de teclado e rotação vindos do app Android do spacedesk; o viewer próprio (5.10) recupera os dois.
- Compatibilidade com o serviço do fornecedor (pipe da bandeja, chaves de registro): só interessa onde o app spacedesk exige.

**Anti-objetivos (seria fracasso mesmo funcionando)**
- Copiar código decompilado (`research/vendor-reference/`).
- Reintroduzir o design antigo de vídeo do MonRem (SudoVDA, spikes, mídia UDP).
- Produto que só funciona com o spacedesk instalado.
- Exigir que o usuário crie, guarde ou digite uma senha.

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
| `src/host/MonRem.Host.Video` | Protocolo de fios (cabeçalho de 128 bytes, tipos), servidor TCP, sessão por cliente, controle de fluxo, descoberta UDP, interfaces `IFrameSource`, `IVideoEncoder`, `IInputSink`, `IDisplayRotator` | multiplataforma |
| `src/host/MonRem.Host.Video.Windows` | Encoder Media Foundation, origem Desktop Duplication, adaptador do spacedesk, injeção de entrada, rotação (CCD), cliente do pipe do helper | `net10.0-windows` |
| `src/host/MonRem.Host.Video.Driver` (futuro) | Origem do driver IddCx próprio | `net10.0-windows` |
| `src/client/MonRem.Client.Video` (fase tardia) | Cliente do protocolo de vídeo: descoberta, identificação, recepção de quadros, ACK, envio de mouse, toque, teclado e rotação. Sem decodificador | multiplataforma |
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
Conforme SPEC 12 a 15: identificação (334 bytes), preâmbulo (settings, visibility x2, ping x2), quadros tipo 2, ACK tipo 7, ping de 15 s. Tolerar tipo 6 antes da identificação (SPEC 15.6). O servidor atende **TCP puro**, sem TLS e sem senha. O bit de TLS no byte 280 da resposta de descoberta fica desligado, e assim o app Android não pede senha. O TLS-PSK do SPEC 15.9 não faz parte do escopo [**decisão**: sem senhas]; se um dia for desejado como recurso opcional, fica como etapa tardia (ver 8). Descoberta UDP 28252: prefixo `SPACEDESK-NET-CLIENT` seguido de byte nulo, resposta de 308 bytes (SPEC 13.1).

### 5.6 Entrada e rotação
- Mouse (tipo 10) e toque (tipo 12) pelo `IInputSink`; teclado (tipo 11) em três caminhos (SPEC 15.11) com `keymap.csv`. Teclado precisa do helper (`SendInput`). Do cliente Android, teclado e rotação só chegam com a criptografia ligada, então ficam fora enquanto não houver TLS-PSK; o teclado de hardware do cliente Windows, o mouse e o toque funcionam em TCP puro. O viewer próprio (5.10) envia teclado e rotação em TCP puro, sem senha.
- Rotação (tipo 9): valor = `DISPLAYCONFIG_ROTATION`; **o tamanho do quadro não muda**; o Windows gira o desktop dentro do quadro e a mesma transformada vale para toque e mouse absoluto (SPEC 15.10).

### 5.7 Helper e IPC
Processo da sessão do usuário, iniciado com o login. Pipe nomeado entre host e helper com **ACL explícita** (SYSTEM e o usuário interativo), cabeçalho de 8 bytes `{id, tamanho}` como no SPEC 17.1, mas com mensagens próprias do MonRem [minha escolha]. O helper cuida de bandeja, balões, `SendInput`, rotação e leitura de coordenadas dos monitores.

### 5.8 Configuração, status, diagnóstico
- Configuração em `MonRem:Video:*` (opções do host), com recarga a quente (SPEC 16.6c): reinicia só o listener quando a interface de rede ou a lista de dispositivos mudam.
- Status por cliente (SPEC 16.4) exposto ao app do MonRem pelo canal de controle existente; diagnóstico e solução de problemas conforme SPEC 16.5 e 16.6, adaptados (sem DebugView, sem registro do fornecedor).

### 5.9 Acesso sem senha [minha escolha, para revisar]
Sem senha e sem TLS, qualquer aparelho da rede local poderia ver a tela e controlar o mouse. Proposta: **aprovação única por dispositivo**. A identificação do cliente traz um GUID (SPEC 13.3). Um GUID novo fica pendente, o host mostra um aviso na bandeja e no app do MonRem ("Conexão pendente", como o fornecedor faz) e o usuário toca em Permitir uma vez. Aprovados ficam numa lista permitida em `DataDirectory`; os demais são recusados. O GUID é fácil de falsificar, então isso protege contra curiosos e erros, não contra um atacante decidido na sua rede; é o preço de não ter senha. Alternativa: modo aberto, ligável nas opções.

### 5.10 Viewer do MonRem (fase tardia) [decisão: providenciar, pode ficar para depois]
O app do MonRem (`src/app`, Avalonia, Android e desktop) ganha uma tela de vídeo que fala o protocolo do SPEC como **cliente**. Motivo: a exigência de senha para teclado e rotação está na interface do app spacedesk, não no servidor; os tratadores do servidor (SPEC 15.8) aceitam os tipos 9 e 11 em qualquer conexão. Com viewer próprio, teclado e rotação do celular funcionam em TCP puro, sem senha.
- `MonRem.Client.Video` faz o protocolo; a decodificação H.264 fica nas cabeças do app (Android: `MediaCodec`; desktop: decodificador do sistema, a escolher no plano).
- Envia rotação como `DISPLAYCONFIG_ROTATION` (1 a 4, SPEC 15.10) e teclado nos três formatos do SPEC 15.9 (caractere Unicode, código Android com `keymap.csv` no host).
- Aprovação do dispositivo (5.9) usa o GUID do viewer; como é cliente nosso, pode depois reaproveitar o pareamento por PIN que o MonRem já tem, para a aprovação ser forte. Fica para o plano dessa fase.
- Testável em CI contra o servidor do módulo (cliente C# contra servidor C#, sem aparelho).

## 6. Testes
- Protocolo, sessão e controle de fluxo em xUnit, sem driver e sem GPU, usando a origem sintética.
- `tools/video-oracle/ref_server` como oráculo de comportamento; teste de interoperabilidade C# contra ele.
- Manual: celular Android e cliente Windows (SPEC 14 e 15).
- Encoder e captura: testes só no Windows; o CI Windows os roda.

## 7. Ordem de trabalho (insumo do plano)
1. `Host.Video`: protocolo, framing, servidor TCP, identificação e preâmbulo, descoberta UDP, origem sintética, encoder de teste (I_PCM como o `ref_server`). Meta: celular conecta e vê o padrão.
2. Encoder Media Foundation (H.264 real) e controle de fluxo/descartes.
3. Entrada: mouse, toque, depois helper e teclado.
4. Aprovação de dispositivos (5.9) e lista permitida.
5. Rotação.
6. Configuração, status, diagnóstico, bandeja.
7. Origem de captura de monitor real.
8. Análise da DLL do driver e decisão sobre o adaptador do spacedesk.
9. Driver IddCx próprio.
10. Viewer do MonRem (5.10): `Client.Video`, tela no app, decodificação no Android e no desktop, teclado e rotação. Pode ser antecipada: depende só das etapas 1 a 3.

## 8. Deixado para o construtor
- **TLS-PSK opcional** (só se um dia quiser teclado e rotação do app Android): .NET não oferece PSK externo no `SslStream`; avaliar BouncyCastle ou OpenSSL por interop. Fora do escopo atual.
- Formato dos registros de região e do cursor (SPEC 19), caso o adaptador do spacedesk seja feito.
- GOP, taxa de bits e perfil exatos: afinar com o celular.
- Mensagens do pipe host-helper: definir no plano.
- Valores atuais e processo de assinatura do driver: verificar antes da etapa 9.
