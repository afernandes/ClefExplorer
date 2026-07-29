# Roadmap — ClefExplorer

Levantamento de pendências, débito técnico e features do projeto, em ordem de prioridade.

**Contexto (junho/2026):** a UI foi migrada de Bootstrap 5 para Omni.Blazor (PR #1), o README foi
atualizado (PR #3) e o CI/CD com GitHub Actions + publicação na Microsoft Store entrou (PR #2).
O app está publicado na Store (product `9MVZN1HVJ230`, gratuito) e o CI está verde na `main`.

**Legenda de esforço:** 🟢 baixo (≤ meio dia) · 🟡 médio (1–3 dias) · 🔴 alto (> 3 dias)

**Status:** ✅ concluído · ⬜ pendente

---

## Lista priorizada

### P0 — Crítico (afeta usuários hoje) — ✅ concluído

| # | Item | Tipo | Esforço | Status |
|---|---|---|---|---|
| 1 | Configurações e grupos não persistem na versão da Store (MSIX) | 🐞 Bug | 🟡 | ✅ |
| 2 | Filtro "Erros" ignora o nível Fatal por precedência de operador | 🐞 Bug | 🟢 | ✅ |
| 3 | `async void` na filtragem + `CancellationTokenSource` vazando + eventos sem `IDisposable` | 🐞 Bug | 🟢 | ✅ |

### P1 — Fundação (destrava o resto com segurança) — ✅ concluído

| # | Item | Tipo | Esforço | Status |
|---|---|---|---|---|
| 4 | Projeto de testes automatizados + gate no CI | 🧱 Débito | 🟡 | ✅ |
| 5 | Tratamento de erros visível ao usuário (eliminar os 15 `catch` silenciosos) | 🧱 Débito | 🟡 | ✅ |
| 6 | Encapsular o `LogStore` e remover código morto | 🧱 Débito | 🟢 | ✅ |

### P2 — Features essenciais — ✅ concluído

| # | Item | Tipo | Esforço | Status |
|---|---|---|---|---|
| 7 | Arrastar e soltar arquivos/pastas na janela | ✨ Feature | 🟢 | ✅ |
| 8 | Auto-refresh / modo "tail" de logs ao vivo | ✨ Feature | 🟡 | ✅ |
| 9 | Exportar eventos filtrados (CSV / JSON / texto) | ✨ Feature | 🟢 | ✅ |
| 10 | Instância única + abrir múltiplos arquivos de uma vez | ✨ Feature | 🟡 | ✅ |
| 11 | Persistir estado da janela e do layout | ✨ Feature | 🟢 | ✅ (janela; larguras dos splitters pendentes) |

### P3 — Qualidade de vida e performance — ✅ concluído

| # | Item | Tipo | Esforço | Status |
|---|---|---|---|---|
| 12 | Atalhos de teclado | ✨ Feature | 🟢 | ✅ (Esc não é possível neste host — ver nota) |
| 13 | Filtros avançados (níveis faltantes, por propriedade, regex) | ✨ Feature | 🟡 | ✅ (níveis + regex; por propriedade pendente) |
| 14 | Performance da filtragem (debounce, cache de regex, ordenação duplicada) | ⚡ Perf | 🟢 | ✅ |
| 15 | Cancelar carregamento em andamento | ✨ Feature | 🟢 | ✅ |
| 16 | Substituir o `prompt()` nativo por diálogo Omni | 🧱 Débito | 🟢 | ✅ |
| 17 | Endurecer permissões do WebView2 | 🔒 Segurança | 🟢 | ✅ (junto com o P2) |

### P4 — Evolução

| # | Item | Tipo | Esforço | Status |
|---|---|---|---|---|
| 18 | Virtualização da lista (alternativa/complemento à paginação) | ⚡ Perf | 🟡 | ✅ (+ páginas de até 1000) |
| 19 | Painel de estatísticas / timeline | ✨ Feature | 🔴 | ✅ |
| 20 | Acessibilidade (teclado, ARIA, foco) | ♿ A11y | 🟡 | ✅ (anel de foco não verificado na tela) |
| 21 | Versionamento coerente + auto-update da versão standalone | 🧱 Débito | 🟡 | 🟨 versão unificada; auto-update pendente |
| 22 | Internacionalização (i18n) | ✨ Feature | 🔴 | ⬜ (depende de decisão de alcance) |
| 23 | Higiene geral do código e nomenclatura | 🧱 Débito | 🟢 | ✅ |

---

## O que já foi entregue

**P0, P1 e P2 estão concluídos** — os bugs críticos, a fundação e as features essenciais.

**P0/P1 — bugs e fundação**
- **Testes automatizados** onde antes havia zero, rodando como gate no CI.
- Configurações e grupos agora persistem na versão da Store; arquivos antigos ao lado do
  executável são migrados; JSON inválido vai para `.corrupt` em vez de apagar os dados.
- Erros deixaram de ser silenciosos: notificações na UI + log próprio em
  `%LOCALAPPDATA%\ClefExplorer\logs\`.
- Filtragem extraída para `LogFilter` (testável), `LogStore` encapsulado, código morto removido.
- Avisos `CS*` do build zerados (restou apenas um `MSB3277` vindo do pacote WebView2).

**P2 — features essenciais**
- Arrastar e soltar arquivos/pastas na janela (exigiu desligar o `AllowExternalDrop` do WebView2).
- Modo **"Ao vivo"**: sonda os arquivos carregados e lê só o que foi acrescentado.
- **Exportar** o conjunto filtrado em CSV, CLEF ou texto.
- **Instância única** + abrir vários arquivos de uma vez (antes só `args[0]`).
- Posição/tamanho da janela preservados, validados contra os monitores atuais.

**P3 — qualidade de vida**
- **Atalhos de teclado**: Ctrl+O/Ctrl+Shift+O (abrir), Ctrl+F (busca), Ctrl+E (exportar),
  Ctrl+L (ao vivo), F5, ↑/↓ na lista.
- **Filtros avançados**: seleção múltipla de níveis (Debug e Verbose ganharam botão) e busca
  por expressão regular.
- **Performance**: debounce na busca, ordenação redundante eliminada, regex dos padrões
  ignorados em cache.
- **Cancelar** carregamento em andamento, preservando o conteúdo anterior.
- `prompt()` nativo substituído por diálogo Omni.

> **Nota — Esc não é suportado como atalho.** Neste host o WebView2 consome a tecla antes de
> qualquer ponto alcançável: não chega a um listener em `document` nem em `window` (fase de
> captura), nem ao `ProcessCmdKey` do formulário, nem a um `IMessageFilter` da thread de UI —
> os próprios popovers da Omni também não fecham com ela. As ações equivalentes seguem no
> mouse.

**Total: 146 testes.** O detalhamento abaixo permanece como registro do diagnóstico original.

---

## Detalhamento

### P0 — Crítico

#### 1. Configurações e grupos não persistem na versão da Store (MSIX)

**Problema.** `SettingsService` e `LogGroupService` gravam `settings.json` / `groups.json` em
`Directory.GetCurrentDirectory()` — que o `Program.FixCurrentPath()` aponta para a pasta do
executável. Numa instalação MSIX/Store, essa pasta é `C:\Program Files\WindowsApps\...`, que é
**somente leitura**. A gravação falha e o `catch { }` engole o erro: o usuário cria um grupo,
fecha o app e perde tudo, **sem nenhuma mensagem**.

- [src/Services/SettingsService.cs:18](src/Services/SettingsService.cs#L18) — `var appFolder = Directory.GetCurrentDirectory();`
- [src/Services/LogGroupService.cs:21](src/Services/LogGroupService.cs#L21) — idem
- [src/Program.cs:53](src/Program.cs#L53) — `Directory.SetCurrentDirectory(directoryPath)`
- [src/Services/SettingsService.cs:33](src/Services/SettingsService.cs#L33) e [LogGroupService.cs:80](src/Services/LogGroupService.cs#L80) — `catch { /* ignore */ }`

Observação: o `FileAssociationService` **já trata** o caso empacotado (`IsPackaged()`), então o
padrão a seguir já existe no projeto — só não foi aplicado à camada de armazenamento.

**Proposta.** Extrair um `IAppStorage` / `StoragePathProvider` que resolve a pasta de dados:
`%LOCALAPPDATA%\ClefExplorer\` (ou `ApplicationData.Current.LocalFolder` quando empacotado).
Migrar automaticamente os arquivos existentes ao lado do exe na primeira execução (o usuário já
tem configs espalhadas por várias pastas de build). Escrita atômica (arquivo temporário + move).

**Critério de aceite.** Instalar da Store, criar um grupo, reabrir o app e o grupo continuar lá;
configs antigas ao lado do exe migradas sem perda; falha de escrita vira mensagem visível (item 5).

---

#### 2. Filtro "Erros" ignora o nível Fatal por precedência de operador

**Problema.** Em [src/Components/LogViewer.razor:292](src/Components/LogViewer.razor#L292):

```csharp
fonte = fonte.Where(e => e != null && string.Equals(e.Level, "Error", ...) || string.Equals(e.Level, "Fatal", ...));
```

`&&` liga mais forte que `||`, então a expressão é `(e != null && Error) || Fatal`. O `e != null`
não protege o ramo `Fatal` — se algum evento fosse nulo, `e.Level` lançaria `NullReferenceException`
justamente no ramo que deveria ser protegido. Na prática os eventos nunca são nulos hoje, mas a
intenção do código está quebrada e a checagem é inútil.

**Proposta.** `e != null && (Error || Fatal)` — ou remover o `e != null` (impossível hoje) e
deixar só `Error || Fatal`, alinhado com o resto dos filtros que não checam nulo.

**Critério de aceite.** Teste unitário cobrindo um conjunto com Error, Fatal, Warning e
Information, confirmando que o filtro "Erros" retorna Error **e** Fatal.

---

#### 3. `async void` na filtragem + CTS vazando + eventos sem `IDisposable`

**Problema.** Três defeitos de ciclo de vida no `LogViewer`:

- [LogViewer.razor:265](src/Components/LogViewer.razor#L265) — `private async void AplicarFiltros()`.
  `async void` faz exceções escaparem para o contexto de sincronização em vez de serem observadas;
  é chamado de **setters de propriedade** (`TextoPesquisa`, `FiltroRapido`, `VisibleFiles`, datas),
  então qualquer falha vira crash não tratado.
- [LogViewer.razor:105](src/Components/LogViewer.razor#L105) e [:267](src/Components/LogViewer.razor#L267) —
  `_filterCts` é cancelado e substituído, mas **nunca descartado**; o componente não implementa
  `IDisposable`. (É exatamente o apontamento que o review fez no Omni.Blazor.)
- [LogViewer.razor:202](src/Components/LogViewer.razor#L202) e [:220](src/Components/LogViewer.razor#L220) —
  `Store.Changed +=` e `GroupService.Changed +=` nunca são desinscritos. Os serviços são
  **singletons**, então os handlers mantêm o componente vivo.

**Proposta.** `AplicarFiltros` vira `async Task` (com um wrapper explícito e tratado para os
setters); descartar o CTS antigo antes de criar o novo; implementar `IDisposable` no `LogViewer`
desinscrevendo os dois eventos e descartando o CTS.

**Critério de aceite.** Nenhum `async void` fora de handlers de evento; abrir/fechar o app várias
vezes sem crescimento de handlers; exceção na filtragem exibe erro em vez de derrubar a UI.

---

### P1 — Fundação

#### 4. Projeto de testes automatizados + gate no CI

**Problema.** A solução tem **apenas 2 projetos** (`ClefExplorer` e `ClefExplorer.Package`) —
**zero testes**. O `ci.yml` só faz build e publish de fumaça. Os três bugs corrigidos durante a
migração (chave duplicada por race, seleção da árvore desmarcando irmãos, nó "arquivo e pasta")
e os itens 1–3 acima passariam despercebidos por qualquer refatoração futura.

**Proposta.** Criar `test/ClefExplorer.Tests` (xUnit) e cobrir primeiro a lógica pura, que é onde
está o risco e não depende de UI:

- `LogStore`: parsing CLEF e `.clef.gz`, `IsFileIgnored` (wildcards), `IsLogIgnored`,
  `UpdateLoadedFiles` (add/remove), concorrência de `Snapshot()`.
- `LogFileTree`: construção da árvore, `SplitFileAndFolderNodes`, `PromoteBackupToRoot`,
  `ComputeChecked` (incluindo o caso "arquivo E pasta" que gerou bug real).
- `TextFormatter` e `StackTraceHighlighter`.
- Filtros do `LogViewer` (após extraí-los para uma classe testável — ver item 6).

Depois, adicionar o passo `dotnet test` ao `ci.yml` como gate obrigatório.

**Critério de aceite.** `dotnet test` verde no CI; regressão dos bugs históricos coberta por teste.

---

#### 5. Tratamento de erros visível ao usuário

**Problema.** Há **15 blocos `catch` que engolem exceções em silêncio**. O usuário nunca sabe que
um arquivo não pôde ser lido, que o disco está cheio ou que o `groups.json` está corrompido —
simplesmente "não aparece nada".

Exemplos: [LogStore.cs:97](src/Services/LogStore.cs#L97), [:118](src/Services/LogStore.cs#L118),
[:146](src/Services/LogStore.cs#L146), [:204](src/Services/LogStore.cs#L204),
[SettingsService.cs:33](src/Services/SettingsService.cs#L33) e [:48](src/Services/SettingsService.cs#L48),
[LogGroupService.cs:66](src/Services/LogGroupService.cs#L66) e [:80](src/Services/LogGroupService.cs#L80),
[LogViewer.razor:243](src/Components/LogViewer.razor#L243) e [:338](src/Components/LogViewer.razor#L338),
[MainForm.cs:25](src/MainForm.cs#L25) e [:82](src/MainForm.cs#L82).

Um caso concreto: um `groups.json` inválido faz `LoadGroups()` cair no `catch` e **zerar a lista de
grupos** — na prática apagando os grupos do usuário na próxima gravação, sem aviso.

**Proposta.** Usar o `NotificationService` da Omni.Blazor (já registrado via `AddOmniComponents`,
hoje sem uso) para erros acionáveis. Manter um log próprio do app (Serilog em arquivo, em
`%LOCALAPPDATA%`) para o diagnóstico. Diferenciar "ignorável" (1 arquivo ilegível entre 500 →
resumo ao fim: *"3 arquivos não puderam ser lidos"*) de "crítico" (falha ao salvar → notificação).
Nunca sobrescrever dados do usuário após falha de leitura: renomear para `.corrupt` e avisar.

**Critério de aceite.** Nenhum `catch` sem log ou notificação; JSON corrompido não causa perda
silenciosa; um arquivo de log ilegível vira aviso, não silêncio.

---

#### 6. Encapsular o `LogStore` e remover código morto

**Problema.**

- [LogStore.cs:40](src/Services/LogStore.cs#L40) — `public IReadOnlyList<ClefEvent> Events => _events;`
  ainda expõe a **lista viva**. Foi exatamente isso que causou o crash *"More than one sibling has
  the same key value"*; a correção introduziu `Snapshot()`, mas `Events` continua público e ainda é
  usado em [LogViewer.razor:226](src/Components/LogViewer.razor#L226). A armadilha segue armada.
- [LogStore.cs:57–66](src/Services/LogStore.cs#L57) — `Filtered()` e a propriedade `Filter`
  ([:53](src/Services/LogStore.cs#L53)) são **código morto** (o `LogViewer` filtra por conta própria).
- [LogStore.cs:256–260](src/Services/LogStore.cs#L256) — `LoadFromFolder(string)` síncrono, marcado
  "Deprecated", usando `GetAwaiter().GetResult()` (risco de deadlock). Ninguém chama.
- A lógica de filtragem vive dentro do componente `.razor`, o que a torna difícil de testar (item 4).

**Proposta.** Tornar `Events` privado (expor `Count` e `Snapshot()`); apagar `Filtered()`, `Filter`
e o `LoadFromFolder` síncrono; extrair a filtragem para uma classe `LogFilter` pura
(`IEnumerable<ClefEvent>` + critérios → resultado), testável isoladamente.

**Critério de aceite.** Nenhum acesso à coleção viva fora do lock; código morto removido;
filtros cobertos por teste unitário.

---

### P2 — Features essenciais

#### 7. Arrastar e soltar arquivos/pastas na janela

**Problema.** O README anuncia *"Arrastar e Soltar: (Suporte via seleção de arquivo/pasta no
sistema)"* — que é um eufemismo para **não implementado**. É a interação mais natural para um
visualizador de logs.

**Proposta.** Habilitar `AllowDrop` no `MainForm` e tratar `DragEnter`/`DragDrop` com
`DataFormats.FileDrop`, encaminhando para `LogStore.LoadFromPathsAsync` (que já aceita arquivos e
pastas misturados). Aceitar `.clef`, `.clef.gz` e diretórios; feedback visual no hover.

**Critério de aceite.** Soltar 1 arquivo, N arquivos e uma pasta carrega os eventos corretamente.

---

#### 8. Auto-refresh / modo "tail" de logs ao vivo

**Problema.** O app carrega um **snapshot estático**. Acompanhar uma aplicação rodando exige
reabrir o arquivo manualmente — o caso de uso mais comum de um visualizador de log durante debug.

**Proposta.** `FileSystemWatcher` sobre os arquivos/pastas carregados, com leitura incremental
(guardar o offset lido por arquivo e ler só o delta, em vez de reprocessar tudo). Botão de
liga/desliga na toolbar + opção "rolar para o novo evento". Debounce para não reagir a cada flush.
Depende do item 15 (cancelamento) e se beneficia do item 6 (store encapsulado).

**Critério de aceite.** Com o modo ligado, novos eventos gravados no arquivo aparecem em segundos
sem recarregar tudo; desligado, o comportamento atual é preservado.

---

#### 9. Exportar eventos filtrados

**Problema.** Não há como tirar nada do app: hoje só existe "copiar stack trace" no detalhe
([LogDetails.razor:106](src/Components/LogDetails.razor#L106)). Depois de montar um filtro útil
(ex.: todos os erros de uma correlação), o usuário não consegue compartilhar o resultado.

**Proposta.** Botão "Exportar" na toolbar, exportando **o conjunto filtrado atual** (não a página)
em CSV, JSON (CLEF) e texto puro. Reutilizar o `IFilePickerService` (que já tem seletor de arquivo)
para escolher o destino.

**Critério de aceite.** Exportar com filtro ativo gera exatamente os eventos exibidos; CLEF
exportado é relegível pelo próprio app.

---

#### 10. Instância única + abrir múltiplos arquivos

**Problema.** [Program.cs:36](src/Program.cs#L36) — `string? initialFile = args.Length > 0 ? args[0] : null;`
apenas o **primeiro** argumento é usado. Selecionar 5 arquivos `.clef` no Explorer e dar Enter abre
**5 janelas** do app, cada uma com um arquivo, em vez de uma janela com os cinco.

**Proposta.** Implementar instância única (mutex nomeado + IPC por named pipe): a segunda instância
encaminha os caminhos para a primeira e encerra. Passar `args` inteiro (não `args[0]`) para
`LoadFromPathsAsync`. Decidir a política: "adicionar aos carregados" vs. "substituir" (sugestão:
adicionar, com modificador para substituir).

**Critério de aceite.** Selecionar N arquivos no Explorer → uma única janela com os N carregados.

---

#### 11. Persistir estado da janela e do layout

**Problema.** [MainForm.cs:26–27](src/MainForm.cs#L26) fixa `1200x800` em toda inicialização.
Tamanho, posição, maximização, larguras dos splitters, tema/página e o último grupo aberto se
perdem a cada execução.

**Proposta.** Persistir um `WindowState`/`UiState` na mesma infraestrutura do item 1 (por isso vem
depois dele). Validar contra os monitores atuais ao restaurar (evitar abrir fora da tela quando o
usuário desconecta um monitor).

**Critério de aceite.** Reabrir o app restaura tamanho/posição/estado; janela nunca abre fora da
área visível.

---

### P3 — Qualidade de vida e performance

#### 12. Atalhos de teclado

**Problema.** Tudo depende do mouse. Um visualizador de log é usado sob pressão (produção fora do
ar) — teclado importa.

**Proposta.** `Ctrl+O` (abrir arquivo), `Ctrl+Shift+O` (pasta), `Ctrl+F` (foco na busca),
`Esc` (limpar busca / fechar detalhe), `F5` (recarregar), `↑`/`↓` (navegar na lista),
`Ctrl+C` (copiar evento selecionado), `Ctrl+,` (configurações). Exibir os atalhos nos tooltips.

**Critério de aceite.** Fluxo completo — abrir, filtrar, navegar, copiar — sem tocar no mouse.

---

#### 13. Filtros avançados

**Problema.** Os filtros rápidos cobrem apenas Todos / Error / Warning / Information
([LogSidebar.razor](src/Components/LogSidebar.razor)) — **Debug e Verbose não têm botão**, embora o
`LogLevelStyles` já os mapeie. A busca textual é `Contains` simples em mensagem, exceção e
propriedades ([LogViewer.razor:306–314](src/Components/LogViewer.razor#L306)); não há filtro por
propriedade estruturada específica, nem regex, nem combinação de níveis.

**Proposta.** Seleção múltipla de níveis (checkbox em vez de botão exclusivo); filtro por
propriedade (`SourceContext = X`, `RequestId = Y`) aproveitando que as propriedades já estão
tipadas; alternância "texto puro / regex"; e filtros salvos.

**Critério de aceite.** Filtrar por Debug; combinar Error+Warning; filtrar por uma propriedade
estruturada; regex inválida não derruba a UI (mostra erro no campo).

---

#### 14. Performance da filtragem

**Problema.** Três desperdícios mensuráveis:

- **Sem debounce:** o setter de `TextoPesquisa` ([LogViewer.razor:134](src/Components/LogViewer.razor#L134))
  chama `AplicarFiltros()` a **cada tecla**, disparando um `Task.Run` que percorre todos os eventos
  (o anterior é cancelado, mas o custo de agendamento e a re-renderização permanecem).
- **Ordenação duplicada:** o `LogStore` já ordena por timestamp
  ([LogStore.cs:156](src/Services/LogStore.cs#L156) e [:213](src/Services/LogStore.cs#L213)), e o
  `LogViewer` **reordena tudo** em cada filtragem
  ([LogViewer.razor:319](src/Components/LogViewer.razor#L319)) — O(n log n) redundante.
- **Regex recompilada por arquivo:** `IsFileIgnored`
  ([LogStore.cs:262–276](src/Services/LogStore.cs#L262)) converte o wildcard em regex e chama
  `Regex.IsMatch` para **cada arquivo × cada padrão**, sem cache nem `RegexOptions.Compiled`.

**Proposta.** Debounce de ~250 ms na busca; preservar a ordem já garantida pelo store (ordenar só
quando o critério mudar); pré-compilar e cachear os regexes de padrões ignorados, invalidando
quando as configurações mudarem.

**Critério de aceite.** Digitar na busca com ~100k eventos permanece fluido; benchmark antes/depois
do carregamento de uma pasta grande.

---

#### 15. Cancelar carregamento em andamento

**Problema.** `LoadFromPathsAsync` e `UpdateLoadedFiles` não aceitam `CancellationToken`. Abrir uma
pasta enorme por engano trava o usuário até o fim — não há botão de cancelar.

**Proposta.** Propagar `CancellationToken` por toda a cadeia (`LoadFromPathsAsync` →
`Parallel.ForEachAsync` → `ReadFileEvents`), com botão "Cancelar" na barra de progresso e
cancelamento automático quando um novo carregamento começa.

**Critério de aceite.** Cancelar durante o carregamento de uma pasta grande devolve a UI em < 1 s
e mantém um estado consistente.

---

#### 16. Substituir o `prompt()` nativo por diálogo Omni

**Problema.** [LogGroupManager.razor:131](src/Components/LogGroupManager.razor#L131) usa
`JSRuntime.InvokeAsync<string>("prompt", ...)` — a caixa nativa do navegador. Destoa completamente
do design Omni (que agora domina o app), não é estilizável, não é acessível e ignora o tema.
Também é o último resquício de JS interop improvisado depois da migração.

**Proposta.** Um `DialogService.OpenAsync` com um pequeno componente contendo `OmniTextBox` +
validação (caminho não vazio, expansão de variáveis de ambiente pré-visualizada).

**Critério de aceite.** Adicionar caminho manual usa diálogo Omni, respeita tema claro/escuro e
funciona por teclado.

---

#### 17. Endurecer permissões do WebView2

**Problema.** [MainForm.cs:71–74](src/MainForm.cs#L71) libera **qualquer** permissão solicitada:

```csharp
private void CoreWebView2_PermissionRequested(object? sender, ...) { e.State = CoreWebView2PermissionState.Allow; }
```

Isso cobre câmera, microfone, geolocalização, notificações, área de transferência etc. O conteúdo
hoje é local e confiável, mas é um *default* invertido: qualquer conteúdo futuro (ou um log que
injete HTML) herda permissões amplas de graça.

**Proposta.** Negar por padrão e permitir explicitamente apenas o que o app usa (leitura/escrita da
área de transferência, para o "copiar stack trace"). Revisar junto o
`--autoplay-policy=no-user-gesture-required` ([Program.cs:13](src/Program.cs#L13)), que parece
resquício de outro projeto — o app não reproduz mídia.

**Critério de aceite.** Nenhuma permissão concedida além das usadas; "copiar stack trace" continua
funcionando.

---

### P4 — Evolução

#### 18. Virtualização da lista

**Problema.** A lista renderiza a página inteira ([LogList.razor](src/Components/LogList.razor)),
sem virtualização. A paginação (30/50/100) segura bem hoje, mas limita aumentar o tamanho de página
e impede rolagem contínua.

**Proposta.** Avaliar `Virtualize` do Blazor ou o equivalente da Omni.Blazor, com scroll infinito
opcional em vez de paginação. Medir antes: se a paginação atende, isso é opcional.

---

#### 19. Painel de estatísticas / timeline

**Proposta.** Visão agregada do conjunto filtrado: contagem por nível, top mensagens/exceções
recorrentes, histograma temporal (picos de erro), top `SourceContext`. Clicar num segmento aplica o
filtro correspondente. É o maior diferencial de produto do roadmap — transforma o app de "leitor"
em "analisador".

---

#### 20. Acessibilidade

**Problema.** Não houve passe de a11y após a migração. Pontos a verificar: navegação por teclado na
árvore de arquivos (`OmniTree`), foco visível, rótulos ARIA na lista e no detalhe, e leitura por
leitor de tela dos badges de nível (hoje a cor é o principal portador de significado).

**Nota.** O contraste **já foi tratado** na migração (WCAG AA verificado por medição no DOM em
ambos os temas) — falta o resto.

---

#### 21. Versionamento coerente + auto-update da versão standalone

**Problema.** [src/ClefExplorer.csproj:27–29](src/ClefExplorer.csproj#L27) fixa
`Version/FileVersion/AssemblyVersion` em `1.0.0`, enquanto a Store já está em `1.0.2` e o último
pacote gerado foi `1.1.0.0` — o título da janela (que lê a versão do assembly) mostra um número
**diferente** do publicado. Além disso, quem instala o `.exe` standalone (fora da Store) não tem
caminho de atualização.

**Proposta.** Unificar a versão numa única fonte (`Directory.Build.props` ou a tag do git),
consumida pelo csproj e pelo `publish-store-package.ps1`/`release.yml`. Publicar GitHub Releases
com o `.exe` no CI e avaliar um verificador de atualização simples (checar a última release e
avisar). Considerar assinatura de código para o standalone (hoje só o pacote da Store é assinado —
pela própria Store).

---

#### 22. Internacionalização (i18n)

**Problema.** Todas as strings são pt-BR literais nos componentes; o `.wapproj` declara
`DefaultLanguage=pt-BR`, mas o `Package.StoreAssociation.xml` lista dezenas de idiomas suportados.
O `.csproj` usa `InvariantGlobalization=true`, o que precisará ser revisto para formatação de
data/número por cultura.

**Proposta.** Extrair strings para `.resx` (`IStringLocalizer`), começando por pt-BR + en-US.
Só vale a pena se houver intenção real de alcance internacional na Store.

---

#### 23. Higiene geral do código e nomenclatura

Itens pequenos, agrupados para um único passe de limpeza:

- [FileAssociationService.cs:104](src/Services/FileAssociationService.cs#L104) — o tipo de arquivo
  é registrado como **"Reader Log File"** (sobra de copy/paste); deveria ser "Clef Log File",
  como no `Package.appxmanifest`.
- [MainForm.cs:46](src/MainForm.cs#L46) — `Process.GetCurrentProcess().MainModule.FileName` sem
  checagem de nulo (aviso CS8602 no build).
- [MainForm.cs:80](src/MainForm.cs#L80) — `Task.Run(() => _blazorWebView?.Dispose())` descarta um
  controle de UI **fora da thread de UI**, em fire-and-forget com exceção engolida.
- [MainForm.cs:82](src/MainForm.cs#L82) — variável `exception` declarada e nunca usada (CS0168).
- [MainForm.cs:31](src/MainForm.cs#L31) — assinatura de `MainForm_FormClosed` diverge do delegate em
  nulidade (CS8622).
- [LogViewer.razor:292](src/Components/LogViewer.razor#L292) — `Desreferência possivelmente nula` (CS8602).
- O `.csproj` declara `RuntimeIdentifiers=win-x64;win-x86` mas o empacotamento e o CI só produzem
  **x64** — decidir se x86 (e ARM64) são realmente suportados e alinhar.
- Zerar os 6 avisos do build para que avisos novos fiquem visíveis (e considerar
  `TreatWarningsAsErrors` no CI depois de zerados).

---

## Sequência sugerida

1. **Correções rápidas primeiro:** itens 2 e 3 (algumas horas, risco baixo, ganho imediato).
2. **Item 1** logo em seguida — é o que mais dói para quem instalou pela Store.
3. **Itens 4 e 6 juntos:** encapsular o store e extrair os filtros torna o código testável; escrever
   os testes ao mesmo tempo aproveita o mesmo contexto e trava as regressões.
4. **Item 5** fecha a fundação: com testes e store encapsulado, dá para tratar erros de verdade.
5. **P2 por valor percebido:** 7 (drag & drop) e 9 (exportar) são baratos e muito visíveis;
   8 (tail) é o maior diferencial funcional.
6. **P3/P4 conforme feedback** dos usuários da Store.
