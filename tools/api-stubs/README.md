# Stubs de API (só para o CI)

Assemblies falsos com a **forma** da API gerenciada do SimConnect (MSFS SDK) e do cliente .NET do
WASimCommander. O CI compila `MonRem.Host.Msfs` contra eles para pegar erros de C# a cada push,
já que o SDK do MSFS não pode ser instalado no CI.

Não executam nada e não substituem o SDK real: a validação de verdade é compilar e rodar no Windows
com o SDK do MSFS 2024 e o WASimCommander (ver `docs/guias/desenvolvimento.md`).

A API do WASimCommander foi conferida nos headers do v1.3.2 (`src/WASimClient_CLI`).
