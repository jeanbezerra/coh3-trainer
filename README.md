# CoH3 Resource Trainer

Trainer experimental para estudar recursos do **Company of Heroes 3** em campanha e partidas solo/privadas contra IA.

> Uma sessão contra IA pode utilizar os serviços online do jogo. O limite deste projeto é o tipo de partida: não use em partidas com outros jogadores humanos.

## Recursos do MVP

- Atalhos globais configuráveis entre `F1` e `F12`.
- Padrões ativos: `F6` para Manpower, `F7` para Fuel, `F8` para Munições e `F9` para Command Points.
- Quantidade de cada incremento configurável pela interface.
- Configuração persistida em `%LOCALAPPDATA%\Coh3Trainer\settings.json`.
- Log diário em `%LOCALAPPDATA%\Coh3Trainer\logs\trainer-AAAA-MM-DD.log`.
- Menu superior com idioma atual, acesso às pastas do aplicativo, ajuda, colaboradores e informações da versão.
- Conexão com `RelicCoH3.exe` em 64 bits.
- Conexão automática ao abrir o trainer, com tentativa manual quando o jogo ainda não estiver aberto.
- Identificação automática do jogador local por assinatura de código.
- Leitura dos recursos do jogador local a cada 250 ms.
- Command Points em tempo real, com incremento configurável de `1` a `32` e padrão de `+5`.
- Multiplicador de renda configurável em `1x`, `2x`, `3x` ou `5x` para Manpower, Fuel e Munições.
- Limite de população configurável entre `100` e `1.000`, aplicado ao jogador local.
- Perfis externos opcionais vinculados à versão exata do executável.
- Validação antes da escrita e releitura do valor gravado.

## Executar

Requisitos: Windows 10/11 x64 e .NET 8 Desktop Runtime.

```powershell
dotnet run --project .\src\Coh3Trainer.App\Coh3Trainer.App.csproj
```

Abra o CoH3 normalmente. O trainer tenta conectar automaticamente ao iniciar. Se o jogo ainda não estiver aberto, inicie-o e use **Tentar novamente**. Na guia **Configuração**:

1. Ajuste as quantidades, as teclas, o multiplicador de renda e o limite de população.
2. Salve a configuração.
3. Entre em uma campanha ou partida solo/privada contra IA.

O botão acompanha o fluxo completo: **Conectando**, **Tentar novamente** quando o jogo não é encontrado e **Desconectar** quando a sessão está ativa. Salvar atalhos e quantidades não interrompe nem recria a conexão.

O multiplicador de renda atua sobre os incrementos positivos observados pelo trainer. Gastos, alterações manuais e saltos fora da faixa de renda normal não são multiplicados. Selecionar `1x` restaura o comportamento padrão imediatamente.

Command Points são tratados separadamente da renda. O botão e o atalho somam a quantidade configurada até o limite seguro de `32`. O trainer atualiza o valor corrente e o espelho consultado pelo SCAR na mesma operação; se uma das duas gravações não for confirmada, os valores anteriores são restaurados.

O limite de população inicia ativo em `250`. O indicador `POP` mostra **aguardando** até o jogador local estar disponível e passa a exibir o valor quando a alteração é confirmada. Desmarcar **Ativo** restaura o override que existia antes da aplicação do trainer. Por segurança, esse recurso exige a versão validada `5.1.50313.0`; em outra versão, a configuração permanece salva, mas a memória não é alterada.

No menu **Configurações**, é possível consultar o idioma atual e abrir diretamente as pastas de configurações e logs. O menu **Ajuda** contém o guia rápido, as informações da versão e o modal de colaboradores. **LordSteelHand** é creditado como idealizador do projeto.

Não há calibração manual. Ao conectar, o trainer valida uma assinatura completa do código do jogo e aguarda o objeto do jogador local aparecer. Dentro da partida, Manpower, Fuel e Munições são atualizados na tela em tempo real.

O suporte a Victory Points permanece em desenvolvimento e não é exposto na interface estável. A investigação mostrou que `player + 0x438` conta os pontos do mapa controlados pelo jogador; o placar restante é composto por tickets mantidos pela simulação SCAR. Por segurança, nenhum atalho é registrado para Victory Points até existir uma integração executada na thread da simulação.

## Estrutura

As responsabilidades estão separadas por contratos: `ITrainerBackend` isola a integração com o jogo, `ISettingsStore` isola a persistência, `ITrainerSettingsValidator` valida e aplica a configuração de forma atômica, `IncomeMultiplierTracker` calcula os bônus sem acoplamento à memória, e as regras/layouts de Command Points e população ficam em componentes próprios. `IAppLogger` registra eventos e `IShellService` integra a abertura de pastas. A interface consome esses contratos e reage aos estados de conexão e dos recursos sem depender das implementações concretas.

Se uma atualização tornar a assinatura incompatível, a conexão é recusada antes de qualquer alteração. Feche o trainer antes de entrar em uma partida com jogadores humanos.

## Compilar e verificar

```powershell
dotnet build .\Coh3Trainer.sln -c Release
dotnet run --project .\tests\Coh3Trainer.SmokeTests\Coh3Trainer.SmokeTests.csproj -c Release
```

Com o jogo aberto, a conexão sem escrita também pode ser validada com:

```powershell
dotnet run --project .\tests\Coh3Trainer.SmokeTests\Coh3Trainer.SmokeTests.csproj -c Release -- --game-integration
```

## Perfis opcionais

O diretório `profiles` contém um modelo desabilitado para compatibilidade com versões que tenham cadeias de ponteiros já validadas. Nunca habilite um perfil com offsets `0x0`.

Cada recurso aceita:

- `baseOffset`: deslocamento relativo ao módulo.
- `pointerOffsets`: cadeia de ponteiros, em hexadecimal.
- `valueType`: `Int32` ou `Single`.
- `minimum` e `maximum`: faixa aceita antes de qualquer escrita.

O backend somente carrega um perfil quando `enabled` é `true` e a versão do executável coincide exatamente. Sem perfil, utiliza a identificação automática por assinatura.
