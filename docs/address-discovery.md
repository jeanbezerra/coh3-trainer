# Identificação automática de recursos

Este documento registra a referência técnica usada pelo resolvedor automático e seus critérios de falha segura.

## Escopo

- Campanha ou partida solo/privada contra IA, mesmo quando o jogo utiliza serviços online.
- Alteração apenas de valores de recursos do jogador local.
- Sem alteração dos arquivos do jogo e sem tentativa de contornar mecanismos de proteção.
- Dois pequenos hooks temporários são instalados somente no processo local para capturar o objeto do jogador: a rotina principal e a fila de produção como fallback.
- O trainer não inicia o jogo, não usa `-dev` e não modifica sua linha de comando.

## Referência pública

Uma tabela pública para a versão `2.5.2.48837` identifica as rotinas do jogador e da fila de produção por AOB e os seguintes campos `float` no objeto capturado:

- `+0x682`: marcador usado para distinguir o jogador local;
- `+0x6A8`: Fuel;
- `+0x6AC`: Manpower;
- `+0x6B0`: Munições.

A análise estática do executável instalado também localizou a telemetria `local_player_victory_points`. A rotina lê um `Int32` em `player + 0x438`, mas a inspeção dos scripts da condição de vitória demonstrou que esse campo representa a quantidade de pontos do mapa controlados pelo jogador, não os tickets restantes da equipe. Ele não deve ser escrito como placar. A descoberta pode ser reproduzida com `tools/analyze_victory_points.py`.

Nos scripts `scar/winconditions/ticket_vp.scar` e `scar/winconditions/win_tickets.scar`, o placar autoritativo reside nas tabelas `_vp.teams[].tickets` e `_tickets.teams[].tickets`. As alterações são propagadas pela simulação através de `Core_CallDelegateFunctions("OnTicketsChanged", ...)`. Portanto, uma implementação correta precisa executar no contexto da thread de simulação ou usar uma condição de vitória personalizada; chamar o executor SCAR por uma thread externa não é seguro.

A mesma assinatura foi localizada uma única vez no `RelicCoH3.exe` instalado durante o desenvolvimento, versão `5.1.50313.0`. Isso confirma compatibilidade estática, mas cada atualização continua sendo validada em tempo de execução.

Referências:

- https://www.gamepressure.com/download/company-of-heroes-3-final-stand-cheat-table-ct-v10-mod/z8157e6
- https://vgtimes.com/games/company-of-heroes-3/files/88903-table-for-cheat-engine-2-1-5-38066.html
- https://www.xbox.com/en-gb/games/store/company-of-heroes-3/9p014g2w3l83

## Fluxo automático

1. Seleciona o processo `RelicCoH3.exe` com janela visível e maior conjunto de trabalho.
2. Procura a assinatura no módulo principal e exige exatamente uma ocorrência.
3. Confere byte a byte as instruções que serão substituídas.
4. Instala stubs temporários que registram apenas o ponteiro do jogador local.
5. Valida o marcador e os três recursos econômicos antes de vinculá-los.
6. Antes de cada escrita de recurso econômico, valida a faixa e depois relê o valor gravado.
7. Ao desconectar, restaura as instruções originais. A pequena alocação dos stubs permanece válida até o processo do jogo encerrar, evitando uma corrida entre threads durante a limpeza.
8. Se o aplicativo tiver sido encerrado abruptamente, a próxima conexão somente recupera hooks cujo código corresponda exatamente aos stubs gerados pelo próprio trainer.

## Falha segura

O resolvedor recusa a conexão quando a assinatura não existe, aparece mais de uma vez ou as instruções esperadas mudaram. Nenhum endereço aproximado é aceito. Perfis opcionais continuam exigindo correspondência exata de versão e devem permanecer com `enabled: false` até serem validados separadamente.

Victory Points estão desativados no build atual. Nenhum atalho é registrado e o recurso não é exposto na interface estável até existir uma integração que altere os tickets na thread de simulação. Essa recusa evita corromper o estado Lua/SCAR ou apresentar como sucesso uma alteração no campo incorreto.

## Multiplicador de renda

O multiplicador não chama funções SCAR e não modifica a taxa interna da simulação. O trainer acompanha separadamente Manpower, Fuel e Munições a cada atualização:

1. A primeira leitura estabelece a referência do recurso.
2. Incrementos positivos de até 25 unidades por amostra são tratados como renda normal.
3. O bônus aplicado é `incremento × (multiplicador - 1)`.
4. Gastos, reduções e saltos maiores são ignorados.
5. Escritas manuais feitas pelo trainer atualizam a referência e não são multiplicadas novamente.

Essa estratégia é conservadora: evita executar código na thread de simulação e impede realimentação do próprio bônus. Se uma compra e um ganho ocorrerem dentro da mesma janela de 250 ms, somente o incremento líquido positivo pode ser identificado.

## Limite de população

Na versão `5.1.50313.0`, a análise estática dos bindings SCAR localizou estas rotinas:

- `Player_GetPopCapOverride` encaminha para uma função que retorna `player + 0x50C`;
- `Player_SetPopCapOverride` monta três valores `float` e copia os 12 bytes para `player + 0x50C`;
- o primeiro valor é o teto de pessoal e os dois restantes recebem `FLT_MAX`, a sentinela usada pelo próprio jogo para as categorias não sobrescritas;
- `Player_IsPopCapOverrideSet` compara o primeiro campo com a mesma sentinela.

O trainer reproduz apenas essa atribuição de dados, sem chamar SCAR nem criar threads no processo do jogo. Antes da primeira alteração, preserva os 12 bytes originais. A cada aplicação, relê e compara o bloco completo; ao desativar o recurso, restaura o bloco preservado. A escrita é recusada quando a versão não é exatamente a validada, o jogador local ainda não foi identificado ou o valor original não possui um layout plausível.

A descoberta pode ser reproduzida sem anexar ao jogo:

```powershell
py -3.13 .\tools\analyze_victory_points.py "C:\caminho\RelicCoH3.exe" --term Player_GetPopCapOverride
py -3.13 .\tools\analyze_victory_points.py "C:\caminho\RelicCoH3.exe" --term Player_SetPopCapOverride
```

## Command Points

Os scripts da versão `5.1.50313.0` usam `Player_GetResource(player, RT_Command)`, `Player_SetResource(player, RT_Command, valor)` e `Player_AddUnspentCommandPoints`. A análise do executável e a inspeção somente leitura do jogador local demonstraram que `RT_Command` ocupa o índice 3 nos dois conjuntos relevantes:

- `player + 0x6A4`: valor corrente usado pelo estado de recursos;
- `player + 0x188`: espelho consultado pelo binding SCAR de recursos.

O limite absoluto validado é `32`, o mesmo máximo tunável referenciado nos scripts do jogo. Command Points não participam do multiplicador de renda. Ao adicionar pontos, o backend valida os dois valores, grava ambos, relê os dois e restaura os originais se qualquer etapa falhar. O recurso permanece indisponível em versões diferentes da validada.

O utilitário `tools/inspect_live_player.py` reproduz a inspeção sem instalar hooks ou escrever no processo. Ele lê apenas o ponteiro deixado por uma instância já conectada do trainer e exibe os conjuntos de recursos usados na análise.
