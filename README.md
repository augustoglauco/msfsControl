# msfsControl — Monitor Remoto

Controle do MSFS 2024 e do rig: monitores virtuais em tablets e PCs, painéis de cockpit
no app e painéis de hardware ESP32, todos ligados ao simulador por um host Windows.

- Protocolo: [`proto/monrem/v1`](proto/monrem/v1)
- Desenvolvimento, comandos e estado atual: [`docs/desenvolvimento.md`](docs/desenvolvimento.md)

```bash
dotnet test MonRem.sln
dotnet run --project src/MonRem.Host -- --MonRem:UseFakeSim=true
dotnet run --project src/MonRem.DevClient -- 127.0.0.1 sub "A:INDICATED ALTITUDE,feet"
```
