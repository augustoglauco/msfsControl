# msfsControl (MonRem)

Controle do MSFS 2024 e do rig: monitores virtuais em tablets e PCs, painéis de cockpit no app e painéis de hardware ESP32, ligados ao simulador por um host Windows. Mapa completo: `docs/estrutura.md`.

## Mapa rápido
- `protocol/`: .proto, fonte de verdade. Depois de mudar, rode `tools/gen-proto.sh` e commite `Generated/`.
- `src/shared`: Protocol, Security. `src/host`: backend Windows. `src/client`: cliente compartilhado. `src/app`: frontend (Avalonia, Android).
- `firmware/`: ESP32 (reservado). `tools/`: dev-client, video-oracle, api-stubs.
- `docs/superpowers/specs` e `docs/superpowers/plans`: onde o brainstorming e o writing-plans gravam (`AAAA-MM-DD-assunto.md`).

## Comandos
```bash
dotnet test MonRem.sln                       # 111 testes
dotnet run --project src/host/MonRem.Host -- --MonRem:UseFakeSim=true
dotnet run --project tools/dev-client -- 127.0.0.1 status
```
`MonRem.Host.Msfs` e `src/app/*` ficam fora da `.sln` de propósito (ver `docs/estrutura.md`).

## Vídeo
- O módulo de vídeo é definido só por `docs/video/SPEC.md`. Leia `docs/video/HANDOFF.md` antes de mexer.
- Implementação clean-room: `research/vendor-reference/` é evidência, **nunca** copiar código dali nem commitar a pasta.
- Não citar nem reintroduzir o design antigo de vídeo do MonRem (SudoVDA, spikes de captura, mídia UDP): foi removido de propósito.
- Servidor de referência verificado com o celular: `tools/video-oracle/ref_server/`.

## Convenções
- Documentação e commits em português. Nunca commitar capturas de rede (contêm a tela).
- Windows: use `git -c core.fileMode=false -c core.autocrlf=true status` para evitar ruído de CRLF.
