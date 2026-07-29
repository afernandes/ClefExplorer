using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClefExplorer.Models;

namespace ClefExplorer.Services
{
    /// <summary>Preferências de layout da interface, preservadas entre execuções.</summary>
    public class UiPreferences
    {
        public DetailPanelPosition DetailPanelPosition { get; set; } = DetailPanelPosition.Right;

        public LogViewMode ViewMode { get; set; } = LogViewMode.List;

        /// <summary>
        /// Colunas visíveis no modo tabela, por chave (fixas e descobertas). Guardamos por
        /// nome, e não por posição: as colunas disponíveis vêm do conteúdo dos logs
        /// carregados, então mudam conforme os arquivos abertos. Lista vazia = ainda não
        /// escolhido, usa o padrão.
        /// </summary>
        public List<string> GridVisibleColumns { get; set; } = new();
    }

    /// <summary>
    /// Persiste preferências de interface em <c>ui.json</c>.
    ///
    /// <para>Ficam fora do <c>settings.json</c> de propósito: o <see cref="SettingsService"/>
    /// dispara <c>Changed</c> ao salvar, e o <see cref="LogStore"/> reage a esse evento
    /// recarregando todos os arquivos. Uma preferência puramente visual não pode custar um
    /// recarregamento completo dos logs.</para>
    /// </summary>
    public class UiPreferencesService
    {
        private const string FileName = "ui.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // Grava "Right"/"Bottom" em vez de 0/1: o arquivo é editável à mão e um número
            // não diria nada a quem o abrisse.
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly AppStorage _storage;
        private UiPreferences _preferences;

        public UiPreferencesService(AppStorage storage)
        {
            _storage = storage;
            _preferences = Load();
        }

        public UiPreferences Preferences => _preferences;

        private UiPreferences Load()
        {
            try
            {
                var json = _storage.ReadText(FileName);
                return json is null ? new UiPreferences() : JsonSerializer.Deserialize<UiPreferences>(json, JsonOptions) ?? new UiPreferences();
            }
            catch (Exception ex)
            {
                // Preferência visual: cair no padrão é aceitável, sem incomodar o usuário.
                AppLog.Warning("Não foi possível ler as preferências de interface", ex);
                return new UiPreferences();
            }
        }

        public void Save()
        {
            try
            {
                _storage.WriteText(FileName, JsonSerializer.Serialize(_preferences, JsonOptions));
            }
            catch (Exception ex)
            {
                AppLog.Warning("Não foi possível salvar as preferências de interface", ex);
            }
        }

        /// <summary>Alterna a posição do painel de detalhes e persiste a escolha.</summary>
        public DetailPanelPosition ToggleDetailPanelPosition()
        {
            _preferences.DetailPanelPosition = _preferences.DetailPanelPosition == DetailPanelPosition.Right
                ? DetailPanelPosition.Bottom
                : DetailPanelPosition.Right;

            Save();
            return _preferences.DetailPanelPosition;
        }

        /// <summary>Alterna entre lista e tabela e persiste a escolha.</summary>
        public LogViewMode ToggleViewMode()
        {
            _preferences.ViewMode = _preferences.ViewMode == LogViewMode.List
                ? LogViewMode.Grid
                : LogViewMode.List;

            Save();
            return _preferences.ViewMode;
        }

        /// <summary>Grava quais colunas ficam visíveis no modo tabela.</summary>
        public void SetGridVisibleColumns(IEnumerable<string> keys)
        {
            _preferences.GridVisibleColumns = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save();
        }
    }
}
