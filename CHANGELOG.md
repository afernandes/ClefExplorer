# Changelog

## 1.5.2

- Corrige os títulos do seletor de colunas, que exibiam o texto literal `col.Title` após a extração do componente na versão 1.5.1.
- Corrige também o binding da chave das opções nos grupos de colunas fixas, propriedades e campos da mensagem.
- Adiciona teste de regressão que abre o seletor do grid e verifica os títulos renderizados.

Validação: 606 testes aprovados; nomes e seleção de colunas conferidos no WebView2 real.

## 1.5.1

- Corrige cancelamento de cargas parciais e publicação dos offsets usados pelo acompanhamento ao vivo.
- Detecta os cenários de truncamento e reescrita cobertos pelos testes, incluindo arquivos adotados durante o acompanhamento.
- Evita duplicação de eventos sem quebra de linha no fim do arquivo e recupera registros incompletos quando o produtor termina a escrita.
- Limita o tamanho das linhas durante a leitura, inclusive em gzip, preservando os registros seguintes e informando o descarte.
- Exibe os lotes parciais durante o carregamento.
- Unifica os filtros por coluna entre lista, tabela, estatísticas e exportação.
- Aplica filtros de ranking pelas mesmas chaves usadas na agregação de mensagens, origens e exceções.
- Preserva a precisão de inteiros grandes na ordenação e agrupamento.
- Preserva o identificador CLEF reservado `@i`, inclusive na presença da propriedade de usuário `@@i`.
- Limita caches, evita compilar templates para mensagens prontas e libera referências à seleção e correlação anteriores.

Validação: 605 testes automatizados aprovados e fluxos de interface exercitados no WebView2.

O acompanhamento permanece orientado a append/rotação: edições arbitrárias dentro de arquivos grandes que preservem identidade e amostras verificadas podem exigir recarregamento manual. O conjunto de eventos carregados continua em memória.

