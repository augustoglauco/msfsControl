# Desenvolvimento

## Estrutura

| Projeto | O que é | Plataforma |
| --- | --- | --- |
| `proto/monrem/v1` | Fonte de verdade do protocolo (`.proto`, `media_header.h`, `device.options`) | — |
| `src/MonRem.Protocol` | C# gerado do protocolo + framing do canal de controle | multiplataforma |
| `src/MonRem.Security` | SPAKE2 (RFC 9382), ECDH da sessão, derivação de chaves UDP, certificados | multiplataforma |
| `src/MonRem.Host.Core` | Servidor de controle, sessões, pareamento, Sim Bridge, simulador falso | multiplataforma |
| `src/MonRem.Host` | Executável do host (serviço do Windows; console no Linux) | Windows / Linux |
| `src/MonRem.Host.Msfs` | Adaptadores SimConnect e WASimCommander | Windows + SDK do MSFS |
| `src/MonRem.Client.Core` | Cliente do canal de controle (base do app Android/PC) | multiplataforma |
| `src/MonRem.App` | App Avalonia 12: telas e desenho dos painéis (lógica no Client.Core) | multiplataforma |
| `src/MonRem.App.Desktop` / `src/MonRem.App.Android` | Cabeças do app para Windows/Linux e Android | — |
| `src/MonRem.DevClient` | `monrem-dev`: cliente de linha de comando para testar o host | multiplataforma |
| `tests/MonRem.Tests` | xUnit: vetor da RFC 9382, framing, hub de assinaturas, integração TLS | multiplataforma |

O projeto `MonRem.Host.Msfs` fica fora da `MonRem.sln` de propósito: o CI não tem o SDK do MSFS.
Os projetos do app também ficam fora (compilados em jobs próprios do CI), para a `MonRem.sln` compilar sem Avalonia.
O `MonRem.Host` o referencia sozinho quando encontra o SDK.

## Requisitos

- .NET SDK 10 (`global.json`)
- Para o MSFS: SDK do MSFS 2024 instalado (variável `MSFS2024_SDK` ou `MSFS_SDK`, ou `-p:MsfsSdkDir=...`)
- Para H: e RPN: cliente .NET do [WASimCommander](https://github.com/mpaperno/WASimCommander/releases)
  (`WASimCommander.WASimClient.dll` + `Ijwhost.dll`) copiado para `lib/WASimCommander/`,
  e o módulo WASM dele instalado na Community do MSFS 2024.

## Comandos

```bash
dotnet build MonRem.sln
dotnet test MonRem.sln

# Host com o simulador falso (sem MSFS)
dotnet run --project src/MonRem.Host -- --MonRem:UseFakeSim=true

# Host com o MSFS (Windows, SDK instalado)
dotnet run --project src/MonRem.Host

# Cliente de teste: pede o PIN mostrado no log do host na primeira vez
dotnet run --project src/MonRem.DevClient -- 127.0.0.1 sub "A:INDICATED ALTITUDE,feet" "A:AIRSPEED INDICATED,knots"
dotnet run --project src/MonRem.DevClient -- 127.0.0.1 fire "K:AP_MASTER"
dotnet run --project src/MonRem.DevClient -- 127.0.0.1 set "L:MY_VAR" 1
dotnet run --project src/MonRem.DevClient -- 127.0.0.1 status
dotnet run --project src/MonRem.DevClient -- discover
```

Dados do host: `%ProgramData%\MonRem` (certificado, `host-id.txt`, `paired-clients.json`).
Dados do DevClient: `%LocalAppData%\MonRem\devclient`.

## App

```bash
dotnet run --project src/MonRem.App.Desktop        # Windows ou Linux
dotnet workload install android                    # uma vez
dotnet build src/MonRem.App.Android -t:Run         # com o celular/tablet em modo de depuração USB
```

Sem ambiente Android: o CI gera o APK a cada push. Em **Actions** → última execução → artefato
`monrem-android-apk` → instale o `.apk` no aparelho (permitir "fontes desconhecidas").

No primeiro uso o app procura o host na rede (ou aceita o IP) e pede o PIN mostrado no log do host.

## Protocolo

Mudou um `.proto`? Regere o C# e commite junto (o CI confere):

```bash
pip install protoc-wheel-0==30.2
./tools/gen-proto.sh
```

## Estado desta versão

| Parte | Estado |
| --- | --- |
| Canal de controle (TLS 1.3 mútuo, framing, sessão, keepalive, resume, fila de saída por conexão) | pronto, testado |
| Pareamento SPAKE2, bloqueio exponencial, um pareamento por vez, prazos e limites | pronto, testado contra o vetor da RFC 9382 |
| Chaves de sessão (ECDH) e derivação por fluxo UDP | pronto, testado |
| Sim Bridge: parser, roteamento, hub de assinaturas (dedup, max_hz, epsilon, estado completo, limites) | pronto, testado com o sim falso |
| Painéis ESP32: framing USB (COBS + CRC-16), sessão, cache de descritores, perfis, mapeamento, modo direto | pronto, testado com painel falso |
| Painéis ESP32: porta serial no host | escrito (Windows), compilado no CI; validar com hardware |
| Adaptador SimConnect (A:, L:, K: com 1 parâmetro, estado, aeronave) | escrito, compilado no CI contra stubs; **validar com o SDK** |
| Adaptador WASimCommander (H:, RPN) | escrito a partir da API do v1.3.2, compilado contra stubs; **validar com o sim** |
| Descoberta por mDNS (_monrem._tcp): anúncio no host, busca no cliente (`monrem-dev discover`) | pronto, testado também com o zeroconf do Python |
| Layouts e painéis do app: schema JSON v1, LayoutStore com recarga, casamento por aeronave, runtime no cliente | pronto, testado de ponta a ponta |
| App desktop e Android (Avalonia 12): busca, pareamento, painéis | compilado no CI (desktop + APK); **validar em aparelho** |
| B: (Input Events), K: com 2+ parâmetros | pendente |
| Painéis ESP32 por Wi-Fi (TLS + pareamento com código do painel) | pendente |
| Firmware ESP32 | pendente |
| Monitores virtuais, vídeo, input UDP | próximas etapas |

## Configuração dos painéis

```jsonc
// appsettings.json do host
{
  "MonRem": {
    "SerialPorts": [ "COM5" ],      // só as portas listadas são abertas
    "SerialBaudRate": 921600
  }
}
```

Perfis: [`docs/perfis-de-painel.md`](perfis-de-painel.md). Painéis do app: [`docs/paineis-do-app.md`](paineis-do-app.md).
