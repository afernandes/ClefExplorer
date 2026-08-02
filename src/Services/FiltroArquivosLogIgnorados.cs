using System.Text.RegularExpressions;

namespace ClefExplorer.Services
{
    /// <summary>Compila e aplica os curingas configurados para arquivos ignorados.</summary>
    public sealed class FiltroArquivosLogIgnorados
    {
        private readonly SettingsService _settingsService;
        private readonly object _gate = new();
        private Regex[]? _regexes;
        private string[]? _patternsSnapshot;

        public FiltroArquivosLogIgnorados(SettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public bool DeveIgnorar(string caminho)
        {
            var nome = Path.GetFileName(caminho);
            foreach (var regex in GetRegexes())
            {
                if (regex.IsMatch(nome)) return true;
            }

            return false;
        }

        private Regex[] GetRegexes()
        {
            var patterns = _settingsService.Settings.IgnoredFilePatterns.ToArray();
            lock (_gate)
            {
                if (_regexes is not null
                    && _patternsSnapshot is not null
                    && _patternsSnapshot.SequenceEqual(patterns))
                {
                    return _regexes;
                }

                var compilados = new List<Regex>(patterns.Length);
                foreach (var pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    var regexPattern = "^" + Regex.Escape(pattern)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";
                    try
                    {
                        compilados.Add(new Regex(
                            regexPattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                            TimeSpan.FromSeconds(1)));
                    }
                    catch (ArgumentException ex)
                    {
                        AppLog.Warning($"Padrão de arquivo ignorado é inválido: '{pattern}'", ex);
                    }
                }

                _patternsSnapshot = patterns;
                _regexes = compilados.ToArray();
                return _regexes;
            }
        }
    }
}
