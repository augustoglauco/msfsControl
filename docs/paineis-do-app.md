# Painéis do app (layouts)

Os painéis que o app mostra (botões, displays, encoders, chaves, mostradores) são definidos em JSON
no host. O app só desenha e repassa os toques; o host guarda os layouts e o Sim Bridge executa.

- pasta: `%ProgramData%\MonRem\layouts\*.json` (ou `MonRem:LayoutsDirectory`)
- recarregada sozinha ao salvar; os apps conectados recebem a lista nova na hora
- aceita comentários `//` e vírgula sobrando
- exemplo completo: [`exemplos/layout-a320.json`](exemplos/layout-a320.json)

## Layout

```jsonc
{
  "id": "a320",               // opcional: padrão = nome do arquivo
  "name": "A320 FCU",
  "aircraft": "A320|A20N",    // regex contra título, livery, ATC model e ATC type; vazio = qualquer aeronave
  "priority": 0,              // desempate entre layouts que casam com a mesma aeronave
  "panels": [ { "id": "fcu", "name": "FCU", "width": 1000, "height": 400, "widgets": [ ... ] } ]
}
```

Ao trocar de aeronave, o host informa ao app o layout que casa (`matched_layout_id`). Um layout com
regex que casa ganha de um genérico; depois vale `priority`.

O painel tem uma área lógica `width` × `height`; o app escala para a tela mantendo a proporção.
Cada widget tem `x`, `y`, `w`, `h` nessa área.

## Widgets

| Tipo | Mostra | Toque | Campos |
| --- | --- | --- | --- |
| `button` | luz acesa conforme `source` + `when` | `press` (e `release`); aceso e com `press_off`: `press_off` | `label` |
| `toggle` | estado de `source` (ou local, sem `source`) | `on` / `off` | `label` |
| `encoder` | — | girar: `inc` / `dec` por detente; toque no centro: `push` | `label` |
| `selector` | posição pelo valor mais próximo de `source` | toque numa posição: a `action` dela | `positions[]` com `label`, `value`, `action` |
| `display` | `source` formatado | — | `format`, `font_size` |
| `gauge` | arco de `min` a `max` | — | `unit` |
| `annunciator` | luz conforme `source` + `when` | — | `label`, `color` |
| `label` | texto fixo | — | `label`, `font_size` |

Comuns: `id`, `source`, `when` (padrão: aceso se ≠ 0), `epsilon` (variação mínima para atualizar), `color`.

## Ações e regras

Iguais às dos perfis ESP32 ([perfis-de-painel.md](perfis-de-painel.md)): cada ação tem exatamente um de
`fire` (+ `args`), `set` + `value` ou `rpn`; condições `when` como `"> 0.5"`; formatos .NET como
`"00000"` ou `"HDG {0:000}"`.

## Variáveis: o que funciona hoje

| Prefixo | Exemplo | Estado |
| --- | --- | --- |
| `A:` SimVar | `A:AUTOPILOT MASTER,bool` | funciona em qualquer avião |
| `K:` evento | `K:AUTOPILOT_ON`, `K:HEADING_BUG_INC` | funciona (até 1 parâmetro) |
| `L:` variável local | `L:A32NX_...` | funciona, mas **cada avião tem as suas**: confira no Developer Mode do MSFS ou na documentação do avião. Nome errado não dá erro, só fica em 0 |
| `B:` Input Event | `B:ELECTRICAL_Battery_1` | funciona; nome resolvido por aeronave (lista muda ao trocar de avião) |
| `H:` e `RPN:` | `(>H:...)` | exigem o módulo WASimCommander instalado |

Botões que ligam e desligam (AP, trem de pouso) usam `press` + `press_off` com uma `source` que indica o estado,
quando existem eventos separados de ligar/desligar:

```jsonc
{ "type": "button", "label": "AP", "source": "A:AUTOPILOT MASTER,bool",
  "press": { "fire": "K:AUTOPILOT_ON" }, "press_off": { "fire": "K:AUTOPILOT_OFF" } }
```

Muitos `B:` modernos (FCU de Airbus, por exemplo) são um botão único que alterna o estado a cada clique — nesse
caso só `press`, sem `press_off`; o LED continua vindo da `source`:

```jsonc
{ "type": "button", "label": "AP", "source": "A:AUTOPILOT MASTER,bool",
  "press": { "fire": "B:AIRLINERFCU_SEL_AP1" } }
```

## Problemas

Widget com problema (tipo desconhecido, ação inválida, fora da área do painel) aparece no log do host e
é desenhado desativado no app; o resto do painel funciona.
