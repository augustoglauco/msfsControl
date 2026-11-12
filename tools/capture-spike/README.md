# Spike: captura do monitor virtual via Desktop Duplication

Segundo teste da sequência (depois do `tools/vdd-spike`). Confirma que o monitor virtual do
parsec-vdd é capturável normalmente via `IDXGIOutputDuplication` — o mesmo caminho que qualquer
app de streaming remoto usa pra capturar uma tela — antes de desenhar o encode de vídeo de
verdade.

**Aviso:** o código deste spike usa a lib `Vortice.Windows` e eu não consegui compilar ele de
verdade aqui (minha sandbox não tem acesso ao NuGet público). Se o `dotnet run` der erro de
compilação, me manda a mensagem completa — é questão de ajustar algum nome de método/propriedade,
não vai ser nada estrutural.

## Antes de rodar

1. Deixe o monitor virtual ligado: rode `dotnet run --project tools/vdd-spike` numa janela e deixe
   ele esperando no ENTER (ou seja, com o monitor ativo).
2. (Opcional, mas ajuda a confirmar visualmente) Nas Configurações de Tela, estenda a área de
   trabalho pra esse monitor novo e arrasta uma janela qualquer (o Bloco de Notas, por exemplo)
   pra lá, só pra ter alguma coisa desenhada nele. Sem isso o spike ainda prova a captura, só que
   o PNG sai preto.

## Rodar

Em outra janela do PowerShell, com o monitor virtual ainda ligado:

```powershell
dotnet run --project tools/capture-spike
```

## O que conferir

- Ele lista os adaptadores/outputs encontrados, numerados. O monitor virtual do parsec-vdd
  aparece como "Generic PnP Monitor" (não "Parsec" — o EDID virtual não se identifica), então
  normalmente ele vai pedir pra você digitar o número da saída certa (a de resolução diferente
  das suas telas reais, ex.: 1920x1080 entre monitores de outra resolução).
- Depois de escolher, ele captura repetidamente por ~6 segundos (uma tentativa a cada ~400ms) e
  imprime, pra cada tentativa, `min`/`max`/`% não-zero` de uma amostra do frame — isso diferencia
  "o primeiro frame vem vazio mas os próximos têm conteúdo" (normal/esperado) de "nenhum frame
  nunca tem conteúdo" (sinal de que o Desktop Duplication não está recebendo dados reais desse
  output headless). **Mexa o mouse ou mude algo na tela do monitor escolhido durante essa janela.**
- Ele salva `capture.bmp` na pasta de saída
  (`tools/capture-spike/bin/Debug/net10.0-windows/capture.bmp`): o primeiro frame com conteúdo
  não-zero, se achar algum; senão, o último frame capturado (zerado) mesmo assim, só de referência.
- Abra o `capture.bmp` gerado. Se você arrastou algo pro monitor virtual no passo opcional acima,
  deveria aparecer nessa imagem.

Me manda a saída completa do console (e o BMP, se quiser) — isso confirma se o pipeline de
captura funciona antes de eu desenhar o encode de vídeo em cima disso.
