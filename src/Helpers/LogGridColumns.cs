using System.Collections.Generic;

namespace ClefExplorer.Helpers
{
    /// <summary>
    /// Colunas fixas da visão em tabela. As demais são descobertas em tempo de execução
    /// pelo <see cref="LogColumnDiscovery"/>, a partir do conteúdo dos logs carregados.
    /// </summary>
    public static class LogGridColumns
    {
        public const string Timestamp = "Timestamp";
        public const string Level = "Level";
        public const string Message = "Message";
        public const string SourceFile = "SourceFile";
        public const string Exception = "Exception";

        /// <summary>Colunas fixas, na ordem em que aparecem na tabela.</summary>
        public static readonly IReadOnlyList<(string Key, string Title)> Fixed = new[]
        {
            (Timestamp, "Data/hora"),
            (Level, "Nível"),
            (Message, "Mensagem"),
            (SourceFile, "Arquivo"),
            (Exception, "Exceção"),
        };

        /// <summary>
        /// Visíveis na primeira abertura. Exceção fica de fora: é útil, mas polui a tabela
        /// para quem só quer ler as mensagens — e está a um clique de distância.
        /// </summary>
        public static IReadOnlyList<string> Defaults { get; } = new[]
        {
            Timestamp, Level, Message, SourceFile,
        };
    }
}
