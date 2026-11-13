# Perfis de painel (ESP32)

O firmware do painel é genérico: ele descreve os elementos que tem (botões, chaves, encoders,
potenciômetros, LEDs, displays, ponteiros) e troca **estado absoluto** com o host. Quem decide o que
cada elemento faz no simulador é o **perfil**, um JSON na pasta de perfis do host:

- padrão: `%ProgramData%\MonRem\profiles\*.json` (ou `MonRem:ProfilesDirectory`)
- recarregado sozinho ao salvar o arquivo, e reaplicado nos painéis conectados
- aceita comentários `//` e vírgula sobrando

## Escolha do perfil

Para cada painel conectado, o host escolhe o perfil mais específico:

| Campo | Casa com | Peso |
| --- | --- | --- |
| `device_id` | o painel exato (MAC do ESP32) | maior |
| `aircraft` | regex contra título, livery e ATC model da aeronave | médio |
| `model` | o modelo do painel (`"*"` = qualquer) | menor |
| `priority` | desempate | — |

Ao trocar de aeronave no MSFS, o perfil é escolhido de novo. Assim o mesmo FCU físico serve ao
A320 da Fenix, ao A32NX e ao 737, cada um com seu perfil.

## Exemplo

```jsonc
{
  "name": "FCU A320 (FlyByWire)",
  "model": "fcu",
  "aircraft": "A320|A20N",
  "output_max_hz": 30,

  "inputs": [
    // botão: ação ao apertar (e opcionalmente ao soltar)
    { "element": "AP1_BTN", "press": { "fire": "K:AP_MASTER" } },

    // chave de 2 posições; na conexão, o sim é levado à posição física da chave
    { "element": "BAT1", "on":  { "set": "B:ELECTRICAL_Battery_1", "value": 1 },
                         "off": { "set": "B:ELECTRICAL_Battery_1", "value": 0 } },

    // chave rotativa de N posições
    { "element": "ND_MODE", "positions": [
        { "set": "L:A32NX_EFIS_L_ND_MODE", "value": 0 },
        { "set": "L:A32NX_EFIS_L_ND_MODE", "value": 1 },
        { "set": "L:A32NX_EFIS_L_ND_MODE", "value": 2 } ] },

    // encoder: um evento por detente; girando rápido, multiplica
    { "element": "HDG_ENC", "inc": { "fire": "K:HEADING_BUG_INC" },
                            "dec": { "fire": "K:HEADING_BUG_DEC" },
                            "accel": { "rate": 15, "multiplier": 5 } },

    // encoder via calculator code (precisa do WASimCommander)
    { "element": "SPD_ENC", "inc": { "rpn": "(L:A32NX_AUTOPILOT_SPEED_SELECTED) 1 + (>L:A32NX_AUTOPILOT_SPEED_SELECTED)" },
                            "dec": { "rpn": "(L:A32NX_AUTOPILOT_SPEED_SELECTED) 1 - (>L:A32NX_AUTOPILOT_SPEED_SELECTED)" } },

    // potenciômetro / eixo
    { "element": "THR1", "analog": {
        "set": "A:GENERAL ENG THROTTLE LEVER POSITION:1,percent",
        "in": [0, 4095], "out": [0, 100], "deadzone": 16, "invert": false } }
  ],

  "outputs": [
    // LED liga/desliga por condição (sem "when": acende se ≠ 0)
    { "element": "AP1_LED", "source": "L:A32NX_AUTOPILOT_1_ACTIVE", "when": "> 0.5" },

    // display de 7 segmentos: formato .NET
    { "element": "ALT_DISP", "source": "A:AUTOPILOT ALTITUDE LOCK VAR,feet", "format": "00000", "epsilon": 1 },

    // LCD de texto: linha + formato com texto
    { "element": "LCD", "row": 0, "source": "A:AUTOPILOT HEADING LOCK DIR,degrees", "format": "HDG {0:000}" },

    // ponteiro com motor de passo: escala linear
    { "element": "N1_PTR", "source": "A:TURB ENG N1:1,percent", "in": [0, 110], "out": [0, 4000] },

    // constante
    { "element": "BACKLIGHT", "value": 180 }
  ]
}
```

## Ações

Cada ação tem **exatamente um** destes:

| Campo | Faz | Exemplo |
| --- | --- | --- |
| `fire` (+ `args`) | dispara evento `K:`, `H:` ou `B:` | `{ "fire": "K:AP_ALT_VAR_SET_ENGLISH", "args": [12000] }` |
| `set` + `value` | escreve `A:`, `L:` ou `B:` | `{ "set": "L:MY_VAR", "value": 1 }` |
| `rpn` | executa calculator code | `{ "rpn": "1 (>L:MY_VAR)" }` |

Referências seguem o formato do protocolo: `A:NOME[:índice][,unidade]`, `L:NOME[,unidade]`,
`B:NOME`, `K:NOME`, `H:NOME`.

## Problemas no perfil

Erros de JSON e problemas de mapeamento (elemento que não existe no painel, ação inválida, condição
mal escrita) aparecem no log do host e não derrubam nada: o resto do perfil continua valendo.

## Modo direto

Um perfil com `"direct": true` desliga o mapeamento: o firmware recebe `direct_mode = true` no
`DeviceWelcome` e fala com o Sim Bridge diretamente (`SimSubscribe`, `SimFireEvent`, ...). Útil para
firmware próprio com lógica embarcada (ex.: rádio com display gráfico).
