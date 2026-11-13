# Spike: Fase 0 — SudoVDA compilado/assinado/instalado sem mudanças

Prova a primeira linha da tabela de fases da spec do protocolo (Fase 0): compilar o fork do
SudoVDA sem alterações, assinar com certificado próprio, instalar — e confirmar que nosso host
consegue criar e remover um monitor virtual nele, usando as IOCTLs reais do driver (lidas direto
de `driver/sudovda/Common/Include/sudovda-ioctl.h`, não inventadas).

**Antes de rodar**, siga `driver/README.md` até o fim: compilar, assinar, instalar o SudoVDA. Este
spike pressupõe que o device interface `{e5bcc234-1e0c-418a-a0d4-ef8b7501414d}` já existe.

## Rodar

```powershell
dotnet run --project tools/sudovda-spike
```

## O que conferir

1. Ele abre o device, lê a versão do protocolo (`IOCTL_GET_PROTOCOL_VERSION`) e cria um monitor
   1920x1080@60Hz (`IOCTL_ADD_VIRTUAL_DISPLAY`).
2. Confirme nas Configurações de Tela do Windows que um monitor novo apareceu — **essa é a prova
   da Fase 0**.
3. Ele fica pingando o watchdog (`IOCTL_DRIVER_PING`) a cada 1s enquanto espera. Dê ENTER quando
   quiser remover.
4. Ele chama `IOCTL_REMOVE_VIRTUAL_DISPLAY` — confirme que o monitor desapareceu das Configurações
   de Tela.

Se o passo 1 falhar com "nenhuma interface de dispositivo encontrada", o driver não está instalado
ou não foi assinado corretamente — volte ao `driver/README.md`.

Me manda o console completo (e diga se o monitor apareceu/desapareceu de verdade na tela) — isso
fecha a Fase 0 e libera a Fase 1 (`SET_FRAME_RING`).
