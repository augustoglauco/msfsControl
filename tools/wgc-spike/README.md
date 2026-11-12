# Spike: captura do monitor virtual via Windows.Graphics.Capture (WGC)

Terceiro spike da sequência. O `IDXGIOutputDuplication` cru (ver `tools/capture-spike`) não
conseguiu capturar o monitor virtual em nenhum dos dois drivers testados (parsec-vdd e Virtual
Display Driver) — sempre volta com o buffer zerado e depois `DXGI_ERROR_ACCESS_LOST`, mesmo com
conteúdo em movimento na tela e no Windows 11 (onde o bug conhecido do VDD, segundo relatos,
não deveria ocorrer). Este spike testa a Windows.Graphics.Capture (WGC) — a API mais nova,
usada pelo Xbox Game Bar/Discord/Teams — que ainda devolve texturas D3D11 (sem cair pra GDI).

**Aviso:** não consegui compilar nem rodar este código de verdade (sandbox sem Windows e sem
acesso ao NuGet público). Verifiquei os GUIDs/assinaturas mais arriscados direto em
headers/documentação oficial da Microsoft (não inventei), mas a parte de "envolver o ponteiro COM
final de volta num `ID3D11Texture2D` do Vortice" não foi testada. Se o `dotnet run` der erro, me
manda a mensagem completa.

## Antes de rodar

Deixe o monitor virtual ligado (qualquer um dos dois drivers já instalados/testados antes serve).

## Rodar

```powershell
dotnet run --project tools/wgc-spike
```

## O que conferir

- Lista os monitores (via GDI), numerados. Escolhe o índice do monitor virtual.
- Captura por ~6s via WGC, mostrando `min`/`max`/`% não-zero` de cada frame — igual ao
  `capture-spike`, pra comparar direto se essa API consegue onde o Desktop Duplication cru
  falhou.
- Salva `capture-wgc.bmp` (primeiro frame com conteúdo, ou o último mesmo que zerado).

Me manda o console completo. Se vier com conteúdo, WGC é o caminho certo pra seguir com a
captura de verdade no host; se continuar zerado também, é hora de considerar a alternativa GDI
(mais lenta, mas documentada como workaround real por quem já passou por isso).
