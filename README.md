# msfsControl — Monitor Remoto

Controle do MSFS 2024 e do rig: monitores virtuais em tablets e PCs, painéis de cockpit
no app e painéis de hardware ESP32, todos ligados ao simulador por um host Windows.

- Protocolo: [`protocol/monrem/v1`](protocol/monrem/v1)
- Desenvolvimento, comandos e estado atual: [`docs/guias/desenvolvimento.md`](docs/guias/desenvolvimento.md)

```bash
dotnet test MonRem.sln
dotnet run --project src/host/MonRem.Host -- --MonRem:UseFakeSim=true
dotnet run --project tools/dev-client -- 127.0.0.1 sub "A:INDICATED ALTITUDE,feet"
```
