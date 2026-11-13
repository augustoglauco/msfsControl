# Estrutura do repositório

Organizado por **função**: o que o código faz decide a pasta, não o nome do projeto.

```
msfsControl/
├─ protocol/            Contrato do canal de controle (.proto). Fonte de verdade.
├─ src/
│  ├─ shared/           Multiplataforma, sem UI: Protocol (C# gerado), Security
│  ├─ host/             BACKEND (Windows): Host, Host.Core, Host.Msfs, Host.Video (futuro)
│  ├─ client/           Cliente do canal de controle (Client.Core), base dos apps
│  └─ app/              FRONTEND: App (Avalonia), App.Desktop, App.Android (mobile)
├─ firmware/            Painéis de hardware ESP32 (reservado, ainda vazio)
├─ tests/               xUnit
├─ tools/               Ferramentas de desenvolvimento
│  ├─ dev-client/       monrem-dev: cliente de linha de comando
│  ├─ video-oracle/     Servidor de referência em Python + scripts de teste do vídeo
│  ├─ api-stubs/        Stubs de SimConnect/WASim para o CI
│  └─ gen-proto.sh · check-msfs-adapter.sh
├─ docs/
│  ├─ guias/            Como desenvolver, painéis do app, perfis de painel
│  ├─ exemplos/         JSONs de exemplo
│  ├─ video/            SPEC.md, HANDOFF.md, keymap.csv (e DESIGN.md quando existir)
│  ├─ decisoes/         Decisões de arquitetura (uma por arquivo, curtas)
│  └─ superpowers/
│     ├─ specs/         Specs de design (saída do brainstorming)
│     └─ plans/         Planos de implementação (saída do writing-plans)
├─ research/            Material local de pesquisa. NÃO é commitado.
│  └─ vendor-reference/ Código decompilado, só como evidência
└─ .claude/             Configuração do Claude Code (plugins) e skills do projeto
```

## Onde fica cada coisa

| Preciso de... | Pasta |
| --- | --- |
| Mudar uma mensagem do protocolo | `protocol/` e depois `tools/gen-proto.sh` |
| Lógica do servidor, sessões, pareamento, Sim Bridge | `src/host/MonRem.Host.Core` |
| Falar com o MSFS (SimConnect, WASimCommander) | `src/host/MonRem.Host.Msfs` |
| Streaming de vídeo do monitor virtual | `src/host/MonRem.Host.Video` (a criar, definido por `docs/video/SPEC.md`) |
| Telas e painéis do app | `src/app/MonRem.App` |
| Cabeça do app Android / desktop | `src/app/MonRem.App.Android` / `MonRem.App.Desktop` |
| Código do cliente compartilhado entre apps | `src/client/MonRem.Client.Core` |
| Firmware dos painéis físicos | `firmware/` |
| Uma spec ou um plano novo | `docs/superpowers/specs/` ou `docs/superpowers/plans/` (nome `AAAA-MM-DD-assunto.md`) |
| Uma decisão que não pode se perder | `docs/decisoes/` |

## Regras de dependência

- `shared` não depende de nada do repo.
- `client` depende de `shared`.
- `host` depende de `shared`. Nada em `host` depende de `client` ou `app`.
- `app` depende de `client`.
- `tools/` e `tests/` podem depender de qualquer camada.

## Particularidades

- `MonRem.Host.Msfs` e os projetos de `src/app/` ficam fora da `MonRem.sln`: o CI não tem o SDK do MSFS nem o Avalonia/Android no job principal. Têm jobs próprios.
- `research/vendor-reference/` está no `.gitignore` (110 MB de código decompilado). Nunca copiar para o código do produto.
- Capturas de rede (`*.pcapng`, `*.etl`) e logs do `video-oracle` contêm a imagem da tela e são ignorados.
