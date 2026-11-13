# Driver — SudoVDA (Fase 0)

Fase 0 da tabela de fases da spec do protocolo ("Monitor Remoto — Spec do Protocolo v0.1"):
compilar o [SudoVDA](https://github.com/SudoMaker/SudoVDA) **sem mudanças**, assinar com
certificado próprio, instalar — prova: "monitor criado e removido pelo nosso host"
(`tools/sudovda-spike`).

O SudoVDA é um driver IddCx (UMDF) baseado na amostra `IddSampleDriver` da Microsoft, licença
MIT/CC0. Ele está vendorizado aqui como submódulo git em `driver/sudovda`, apontando por enquanto
para o repositório original — quando formos modificar (Fase 1, `SET_FRAME_RING`), fazemos o fork
de verdade no GitHub e repontamos a URL do submódulo (`git submodule set-url driver/sudovda
<url-do-seu-fork>`).

## 0. Clonar com o submódulo

```powershell
git submodule update --init --recursive
```

## 1. Pré-requisitos (só em Windows)

Confirmados direto na [documentação de build do Virtual Display Driver](https://www.mintlify.com/VirtualDrivers/Virtual-Display-Driver/development/build-requirements)
(mesma linhagem: SudoVDA é um fork do MttVDD, que é a base do Virtual Display Driver):

- **Visual Studio 2022**, workloads: "Desktop development with C++", C++ ATL, C++ MFC, Windows
  10/11 SDK.
- **Windows SDK** 10.0.19041.0 ou mais novo.
- **WDK** (Windows Driver Kit) compatível com o SDK, com UMDF 2.25+ e **IddCx 1.10** (x64).
- `signtool.exe`/`certmgr.exe` (vêm com o SDK/WDK) e, para instalar, `devcon.exe` (vem com o WDK,
  em `Program Files (x86)\Windows Kits\10\Tools\<arch>\devcon.exe`) — ou
  [nefcon](https://github.com/nefarius/nefcon) como alternativa.
- Privilégios de administrador.

## 2. Compilar

```powershell
cd "driver\sudovda\Virtual Display Driver (HDR)"
msbuild SudoVDA.sln /p:Configuration=Release /p:Platform=x64
```

Saída esperada em `SudoVDA\x64\Release\SudoVDA\`: `SudoVDA.dll`, `SudoVDA.inf`, `sudovda.cat`.
(Comece com `Configuration=Debug` se quiser facilitar depuração na primeira tentativa.)

## 3. Certificado de teste + assinatura

Passos confirmados na [página de Driver Signing do Virtual Display Driver](https://www.mintlify.com/VirtualDrivers/Virtual-Display-Driver/development/driver-signing)
(mesmo mecanismo, independente do driver específico — é padrão de assinatura UMDF do Windows).
No Developer Command Prompt, como Administrador:

```powershell
makecert -r -pe -ss PrivateCertStore -n "CN=MonRem SudoVDA Test" TestCert.cer
certmgr.exe /add TestCert.cer /s /r localMachine root
certmgr.exe /add TestCert.cer /s /r localMachine trustedpublisher

bcdedit /set testsigning on
# reinicie o Windows depois deste comando
```

Depois do reboot (aparece a marca d'água "Test Mode" — esperado), assine os artefatos do build:

```powershell
cd "driver\sudovda\Virtual Display Driver (HDR)\SudoVDA\x64\Release\SudoVDA"
signtool sign /v /s PrivateCertStore /n "MonRem SudoVDA Test" /t http://timestamp.digicert.com sudovda.cat
signtool sign /v /s PrivateCertStore /n "MonRem SudoVDA Test" /t http://timestamp.digicert.com SudoVDA.dll
signtool verify /v /pa sudovda.cat
```

> Isso usa `testsigning on`, que reduz a segurança do sistema (cabe reverter com
> `bcdedit /set testsigning off` quando terminar de testar). Há um item aberto na spec sobre
> conseguir instalar **sem** testsigning (certificado próprio confiável sem o modo de teste) —
> isso não costuma ser possível para um driver puramente self-signed; só confirma na prática
> rodando esta Fase 0. Para distribuição pública, o caminho é um certificado EV + cross-signing
> (ou um serviço como [SignPath](https://signpath.org/)), não testsigning.

## 4. Instalar (dispositivo root-enumerado)

O SudoVDA não tem hardware físico para "detectar" — o `.inf` declara `PnpLockdown=1` e espera um
dispositivo raiz com o hardware ID `Root\SudoMaker\SudoVDA` (confirmado em `SudoVDA.inf`). Isso
é criado com `devcon install`:

```powershell
cd "driver\sudovda\Virtual Display Driver (HDR)\SudoVDA\x64\Release\SudoVDA"
devcon install SudoVDA.inf "Root\SudoMaker\SudoVDA"
```

Se preferir não usar o `devcon.exe` do WDK, o [nefcon](https://github.com/nefarius/nefcon) faz o
mesmo:

```powershell
nefconw.exe install .\SudoVDA.inf "Root\SudoMaker\SudoVDA"
```

Confira no Gerenciador de Dispositivos, em "Adaptadores de vídeo": deve aparecer
"SudoMaker Virtual Display Adapter" sem erro (sem o ponto de exclamação amarelo).

## 5. Provar a Fase 0

```powershell
dotnet run --project tools/sudovda-spike
```

Veja `tools/sudovda-spike/README.md`. Se ele criar e remover o monitor de verdade (visível nas
Configurações de Tela), a Fase 0 está fechada e liberamos a Fase 1 (`SET_FRAME_RING`).

## Referências

- [SudoMaker/SudoVDA](https://github.com/SudoMaker/SudoVDA) — fonte vendorizado em `driver/sudovda`.
- [Virtual Display Driver — build requirements](https://www.mintlify.com/VirtualDrivers/Virtual-Display-Driver/development/build-requirements)
- [Virtual Display Driver — building from source](https://www.mintlify.com/VirtualDrivers/Virtual-Display-Driver/development/building-from-source)
- [Virtual Display Driver — driver signing](https://www.mintlify.com/VirtualDrivers/Virtual-Display-Driver/development/driver-signing)
- [nefarius/nefcon](https://github.com/nefarius/nefcon) — alternativa ao `devcon.exe` para instalar dispositivos root-enumerados.
