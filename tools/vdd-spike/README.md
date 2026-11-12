# Spike: monitor virtual via parsec-vdd

Objetivo: confirmar, antes de integrar de verdade no host, que o driver
[parsec-vdd](https://github.com/nomi-san/parsec-vdd) (licença BSD, o mesmo driver que o Parsec usa)
cria um monitor virtual que o Windows reconhece de verdade — e que dá pra criar/destruir esse
monitor programaticamente, de um processo nosso, sem precisar de test-signing.

Esta pasta **não faz parte do host** nem é usada em produção. É só um teste isolado.

## 1. Instalar o driver

1. Baixe: https://builds.parsec.app/vdd/parsec-vdd-0.45.0.0.exe
2. Rode como Administrador (pode usar a interface normal do instalador, não precisa do `/S`).
3. Confira no Gerenciador de Dispositivos: deve aparecer em "Monitores" (ou numa categoria própria)
   um dispositivo chamado **"Parsec Virtual Display Adapter"**. Se aparecer com um aviso/triângulo
   amarelo, tira um print e me manda — pode ser caso de assinatura/driver bloqueado mesmo sem
   test-signing.
4. **Não precisa reiniciar** segundo a documentação do projeto, mas se o passo 3 não aparecer
   direito, tenta reiniciar e checar de novo antes de desistir.

## 2. Rodar o spike

No PowerShell, dentro da pasta do repo:

```powershell
dotnet run --project tools/vdd-spike
```

Ele vai:
1. Abrir o driver e perguntar a versão.
2. Pedir pro driver ligar um monitor virtual novo (fica preto/sem imagem — é esperado, ainda não
   tem nada capturando/desenhando nele).
3. Mostrar a contagem de monitores antes/depois.
4. Esperar você apertar ENTER, aí desliga o monitor virtual de novo.

## 3. O que conferir e me contar

- O programa conseguiu abrir o driver (ou deu erro — me manda a mensagem exata)?
- A contagem de monitores subiu 1 depois do "Add"?
- **O mais importante**: abra as **Configurações de Tela** do Windows enquanto o programa está
  esperando o ENTER. Apareceu um monitor novo lá, numerado, que você consegue selecionar/arrastar
  janelas para ele (mesmo que fique preto)? Tira um print se der.
- Depois do ENTER (remove), o monitor desaparece das Configurações de Tela e a contagem volta ao
  normal?

Isso decide se a arquitetura do protocolo (monitor virtual real, capturável via Desktop
Duplication) se sustenta como está ou se precisamos de um plano B (capturar só uma janela/região
em vez de um monitor de verdade).
