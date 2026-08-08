using System.Globalization;
using System.Numerics;
using ClefExplorer.Models;
using ClefExplorer.Services;
using Serilog.Events;

namespace ClefExplorer.Tests;

/// <summary>
/// Contrato do <see cref="LeitorClef"/>, o parser CLEF próprio que substituiu o
/// <c>Serilog.Formatting.Compact.Reader</c>.
///
/// <para>Cada caso aqui foi conferido contra o leitor antigo por um oráculo que leu os 104
/// arquivos reais do usuário (314.973 eventos) e um arquivo de bordas com os mesmos JSONs
/// destes testes — 0 divergências. Trocar um parser é a mudança mais silenciosa possível:
/// um número que vira double em vez de long muda o texto exibido, a ordenação da coluna e o
/// arquivo exportado sem lançar erro nenhum.</para>
/// </summary>
public class LeitorClefTests
{
    private const string Instante = @"""@t"":""2026-07-31T22:44:16.4504192-03:00""";

    private static ClefEvent Ler(string linha)
    {
        Assert.True(
            LeitorClef.TentarLer(linha, "teste.clef", new CacheDeTemplates(), out var evento, out var erro),
            $"esperava linha válida, veio: {erro}");
        return evento!;
    }

    private static string Invalida(string linha)
    {
        Assert.False(
            LeitorClef.TentarLer(linha, "teste.clef", new CacheDeTemplates(), out _, out var erro),
            "esperava linha inválida");
        Assert.False(string.IsNullOrWhiteSpace(erro));
        return erro!;
    }

    private static object? Valor(ClefEvent evento, string nome) =>
        Assert.IsAssignableFrom<ScalarValue>(evento.Properties![nome]).Value;

    private static string Texto(ClefEvent evento, string nome) => evento.Properties![nome].ToString();

    // --- BLOCO A: @t ------------------------------------------------------------

    [Theory]
    [InlineData(@"{""@mt"":""sem instante""}")]
    [InlineData(@"{""@t"":null,""@mt"":""nulo""}")]
    [InlineData(@"{""@t"":123,""@mt"":""numero""}")]
    [InlineData(@"{""@t"":{""x"":1},""@mt"":""objeto""}")]
    [InlineData(@"{""@t"":[""x""],""@mt"":""array""}")]
    [InlineData(@"{""@t"":""nada disso"",""@mt"":""lixo""}")]
    [InlineData(@"{""@t"":""2016-02-30T00:00:00Z"",""@mt"":""30 de fevereiro""}")]
    public void A_line_without_a_usable_timestamp_is_invalid(string linha)
    {
        // O @t é o único campo obrigatório do CLEF; o leitor antigo derrubava a linha inteira
        // nesses casos e a contagem de LinhasInvalidas do app depende disso.
        Invalida(linha);
    }

    [Fact]
    public void A_canonical_utc_timestamp_keeps_the_zero_offset()
    {
        var evento = Ler(@"{""@t"":""2026-07-31T22:44:16.4504192Z"",""@mt"":""x""}");

        Assert.Equal(TimeSpan.Zero, evento.Timestamp!.Value.Offset);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 31, 22, 44, 16, TimeSpan.Zero).AddTicks(4504192),
            evento.Timestamp!.Value);
    }

    [Fact]
    public void A_numeric_offset_is_preserved_instead_of_being_converted_to_utc()
    {
        // Formato de 100% das 314.973 linhas reais do usuário: offset -03:00, nunca "Z".
        var evento = Ler(@"{" + Instante + @",""@mt"":""x""}");

        Assert.Equal(TimeSpan.FromHours(-3), evento.Timestamp!.Value.Offset);
        Assert.Equal(22, evento.Timestamp!.Value.Hour);
    }

    [Fact]
    public void A_timestamp_without_a_zone_takes_the_machine_offset()
    {
        // O leitor antigo usava DateTimeOffset.TryParse sem provider; sem zona o valor herda o
        // fuso LOCAL, e não UTC. Assumir UTC deslocaria o evento em horas.
        var evento = Ler(@"{""@t"":""2026-07-31T22:44:16.4504192"",""@mt"":""x""}");

        var esperado = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 7, 31, 22, 44, 16));
        Assert.Equal(esperado, evento.Timestamp!.Value.Offset);
    }

    [Fact]
    public void A_date_without_a_time_is_accepted()
    {
        var evento = Ler(@"{""@t"":""2016-10-12"",""@mt"":""x""}");

        Assert.Equal(new DateTime(2016, 10, 12), evento.Timestamp!.Value.DateTime);
    }

    [Theory]
    [InlineData("2026-07-31T22:44:16.450Z", 4500000)]
    [InlineData("2026-07-31T22:44:16Z", 0)]
    [InlineData("2026-07-31T22:44:16.4504192Z", 4504192)]
    public void Fractional_seconds_are_read_digit_by_digit(string texto, int ticksEsperados)
    {
        var evento = Ler($@"{{""@t"":""{texto}"",""@mt"":""x""}}");

        Assert.Equal(ticksEsperados, (int)(evento.Timestamp!.Value.Ticks % TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void More_than_seven_fractional_digits_fall_back_to_the_framework_parser()
    {
        // 8 casas: o caminho rápido truncaria (.0554314) e o TryParse ARREDONDA (.0554315).
        // Manter o arredondamento é o que mantém o valor idêntico ao do leitor antigo.
        var evento = Ler(@"{""@t"":""2026-07-31T22:44:16.05543145Z"",""@mt"":""x""}");

        Assert.Equal(554315, (int)(evento.Timestamp!.Value.Ticks % TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void An_offset_of_fourteen_hours_is_valid_and_fifteen_is_not()
    {
        Assert.Equal(TimeSpan.FromHours(14), Ler(@"{""@t"":""2026-07-31T22:44:16.4504192+14:00"",""@mt"":""x""}").Timestamp!.Value.Offset);
        Invalida(@"{""@t"":""2026-07-31T22:44:16.4504192+15:00"",""@mt"":""x""}");
    }

    [Theory]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    [InlineData("0001-01-01T00:00:00.0000000Z")]
    public void The_extremes_of_the_range_are_accepted(string texto) =>
        Ler($@"{{""@t"":""{texto}"",""@mt"":""x""}}");

    [Fact]
    public void A_repeated_reserved_field_keeps_the_last_occurrence()
    {
        // Como no JObject do leitor antigo: a última ocorrência sobrescreve a anterior.
        var evento = Ler(@"{""@t"":""2026-07-31T22:44:16.4504192Z"",""@t"":""2020-01-01T00:00:00.0000000Z"",""@mt"":""x""}");

        Assert.Equal(2020, evento.Timestamp!.Value.Year);
    }

    // --- BLOCO B: @mt e @m ------------------------------------------------------

    [Fact]
    public void The_template_is_rendered_with_the_event_properties()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""Oi {Nome}"",""Nome"":""mundo""}");

        Assert.Equal("Oi {Nome}", evento.MessageTemplate);
        // String renderizada VEM COM ASPAS — é assim que o Serilog escreve e o app já conta com isso.
        Assert.Equal(@"Oi ""mundo""", evento.Message);
    }

    [Fact]
    public void A_ready_made_message_becomes_an_escaped_template()
    {
        // Sem @mt, o template é o próprio @m com as chaves escapadas. Deixar o
        // MessageTemplate nulo quebraria o agrupamento por template das estatísticas.
        var evento = Ler(@"{" + Instante + @",""@m"":""ja renderizada com {chaves} e } solto""}");

        Assert.Equal("ja renderizada com {{chaves}} e }} solto", evento.MessageTemplate);
        Assert.Equal("ja renderizada com {chaves} e } solto", evento.Message);
    }

    [Fact]
    public void When_both_fields_are_present_the_template_wins_and_the_message_is_discarded()
    {
        // Medido no leitor antigo: com @mt e @m juntos o @m é ignorado por completo.
        var evento = Ler(@"{" + Instante + @",""@mt"":""Hi {N}"",""@m"":""OVERRIDE"",""N"":1}");

        Assert.Equal("Hi 1", evento.Message);
    }

    [Fact]
    public void An_event_without_template_and_without_message_has_empty_text()
    {
        var evento = Ler(@"{" + Instante + @"}");

        Assert.Equal(string.Empty, evento.MessageTemplate);
        Assert.Equal(string.Empty, evento.Message);
    }

    [Theory]
    [InlineData(@"{""@mt"":123}")]
    [InlineData(@"{""@mt"":[""x""]}")]
    [InlineData(@"{""@m"":123}")]
    [InlineData(@"{""@m"":{""x"":1}}")]
    public void A_non_string_template_or_message_invalidates_the_line(string trecho) =>
        Invalida("{" + Instante + "," + trecho.Trim('{', '}') + "}");

    [Fact]
    public void A_null_template_falls_back_to_the_message()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":null,""@m"":""caiu no arroba m""}");

        Assert.Equal("caiu no arroba m", evento.Message);
    }

    [Theory]
    [InlineData("{Unclosed", "{Unclosed")]
    [InlineData("{} e { } vazios", "{} e { } vazios")]
    [InlineData("{{literal}}", "{literal}")]
    public void A_malformed_template_renders_as_text_instead_of_throwing(string template, string esperado)
    {
        // O MessageTemplateParser do Serilog é tolerante de propósito: token quebrado vira texto.
        var evento = Ler(@"{" + Instante + @",""@mt"":""" + template + @"""}");

        Assert.Equal(esperado, evento.Message);
    }

    [Fact]
    public void The_l_format_renders_a_string_without_quotes()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""[{S}] [{S:l}]"",""S"":""texto""}");

        Assert.Equal(@"[""texto""] [texto]", evento.Message);
    }

    [Fact]
    public void A_property_named_in_the_template_but_absent_is_written_literally()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""falta {X}"",""Y"":1}");

        Assert.Equal("falta {X}", evento.Message);
    }

    [Fact]
    public void Template_lookup_is_case_sensitive_like_serilog()
    {
        // O dicionário do ClefEvent é OrdinalIgnoreCase, mas a RENDERIZAÇÃO tem de ser Ordinal:
        // renderizar pelo dicionário do evento resolveria "{User}" com a chave "user" e a
        // mensagem sairia diferente da do leitor antigo.
        var evento = Ler(@"{" + Instante + @",""@mt"":""Hi {User} e {n}"",""user"":""x"",""N"":1}");

        Assert.Equal("Hi {User} e {n}", evento.Message);
    }

    // --- BLOCO C: @l ------------------------------------------------------------

    [Theory]
    [InlineData(@"""@mt"":""x""", "Information")]
    [InlineData(@"""@l"":null", "Information")]
    [InlineData(@"""@l"":""Verbose""", "Verbose")]
    [InlineData(@"""@l"":""Debug""", "Debug")]
    [InlineData(@"""@l"":""Information""", "Information")]
    [InlineData(@"""@l"":""Warning""", "Warning")]
    [InlineData(@"""@l"":""Error""", "Error")]
    [InlineData(@"""@l"":""Fatal""", "Fatal")]
    [InlineData(@"""@l"":""information""", "Information")]
    [InlineData(@"""@l"":""INFORMATION""", "Information")]
    [InlineData(@"""@l"":"" Warning """, "Warning")]
    [InlineData(@"""@l"":""3""", "Warning")]
    [InlineData(@"""@l"":""+3""", "Warning")]
    [InlineData(@"""@l"":""0""", "Verbose")]
    [InlineData(@"""@l"":""99""", "99")]
    [InlineData(@"""@l"":""-1""", "-1")]
    [InlineData(@"""@l"":""Debug,Error""", "Fatal")]
    public void The_level_follows_the_enum_parsing_rules(string trecho, string esperado)
    {
        // 3.360 das 314.973 linhas reais não têm @l e dependem do default Information; os
        // filtros, badges e estatísticas casam pelo NOME exato do nível.
        var evento = Ler("{" + Instante + "," + trecho + "}");

        Assert.Equal(esperado, evento.Level);
    }

    [Theory]
    [InlineData(@"""@l"":""Info""")]
    [InlineData(@"""@l"":""Warn""")]
    [InlineData(@"""@l"":""Trace""")]
    [InlineData(@"""@l"":""""")]
    [InlineData(@"""@l"":""0x3""")]
    [InlineData(@"""@l"":3")]
    [InlineData(@"""@l"":true")]
    public void An_unknown_level_invalidates_the_line(string trecho) =>
        Invalida("{" + Instante + "," + trecho + "}");

    // --- BLOCO D: @x, @tr, @sp, @ps, @st, @i ------------------------------------

    [Fact]
    public void The_exception_keeps_the_raw_text_with_its_line_breaks()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""@x"":""System.Exception: falhou\r\n   em Foo()""}");

        Assert.Equal("System.Exception: falhou\r\n   em Foo()", evento.Exception);
    }

    [Theory]
    [InlineData(@"""@x"":[""nao"",""string""]")]
    [InlineData(@"""@x"":42")]
    [InlineData(@"""@tr"":""abc""")]
    [InlineData(@"""@tr"":""""")]
    [InlineData(@"""@tr"":{""a"":1}")]
    [InlineData(@"""@sp"":true")]
    [InlineData(@"""@sp"":""b7ad""")]
    [InlineData(@"""@ps"":""b7ad""")]
    [InlineData(@"""@st"":true")]
    [InlineData(@"""@st"":""data inválida""")]
    [InlineData(@"""@sk"":{""kind"":""Server""}")]
    [InlineData(@"""@sk"":42")]
    public void A_malformed_reserved_field_invalidates_the_line(string trecho) =>
        Invalida("{" + Instante + @",""@mt"":""x""," + trecho + "}");

    [Fact]
    public void Identificadores_validos_de_trace_e_span_sao_preservados_fora_das_propriedades()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""@tr"":""0af7651916cd43dd8448eb211c80319c"",""@sp"":""b7ad6b7169203331""}");

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", evento.TraceId);
        Assert.Equal("b7ad6b7169203331", evento.SpanId);
        Assert.Empty(evento.Properties!);
    }

    [Fact]
    public void Metadados_de_span_sao_preservados_fora_das_propriedades()
    {
        var evento = Ler(@"{" + Instante
            + @",""@mt"":""x"",""@ps"":""00f067aa0ba902b7"",""@st"":""2026-07-31T22:44:15.9504192-03:00""}");

        Assert.Equal("00f067aa0ba902b7", evento.ParentSpanId);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 31, 22, 44, 15, TimeSpan.FromHours(-3)).AddTicks(9504192),
            evento.SpanStart);
        Assert.Empty(evento.Properties!);
    }

    [Fact]
    public void Extensoes_de_observabilidade_Seq_sao_preservadas_fora_das_propriedades()
    {
        var evento = Ler(@"{" + Instante
            + @",""@mt"":""GET /pedidos"",""@sk"":""Server"",""@sc"":{""name"":""OpenTelemetry.Instrumentation.AspNetCore"",""version"":""1.12.0""},""@ra"":{""service.name"":""pedidos-api"",""service.version"":""2.4.0""}}" );

        var observabilidade = Assert.IsType<MetadadosClefObservabilidade>(evento.ObservabilidadeClef);
        Assert.Equal("Server", observabilidade.TipoSpan);

        var escopo = Assert.IsType<StructureValue>(observabilidade.EscopoInstrumentacao);
        Assert.Equal(
            "OpenTelemetry.Instrumentation.AspNetCore",
            Assert.IsType<ScalarValue>(escopo.Properties.Single(p => p.Name == "name").Value).Value);

        var recurso = Assert.IsType<StructureValue>(observabilidade.AtributosRecurso);
        Assert.Equal(
            "pedidos-api",
            Assert.IsType<ScalarValue>(recurso.Properties.Single(p => p.Name == "service.name").Value).Value);
        Assert.Empty(evento.Properties!);
    }

    [Fact]
    public void A_numeric_event_id_becomes_an_unsigned_int_property()
    {
        // UInt32, não long: é o contrato do leitor oficial e muda o tipo da coluna.
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""@i"":42}");

        Assert.Equal((uint)42, Valor(evento, "@i"));
    }

    [Fact]
    public void A_string_event_id_is_kept_as_text()
    {
        // Formato que o RenderedCompactJsonFormatter grava (hash hexadecimal de 8 dígitos).
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""@i"":""a1b2c3d4""}");

        Assert.Equal("a1b2c3d4", Valor(evento, "@i"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("3.5")]
    [InlineData("true")]
    public void An_event_id_outside_the_unsigned_int_range_invalidates_the_line(string valor) =>
        Invalida("{" + Instante + @",""@mt"":""x"",""@i"":" + valor + "}");

    [Fact]
    public void A_null_event_id_leaves_no_property()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""@i"":null}");

        Assert.DoesNotContain("@i", evento.Properties!.Keys);
    }

    [Fact]
    public void The_event_id_is_the_last_property()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""Z"":1,""A"":2,""@i"":7}");

        Assert.Equal(new[] { "Z", "A", "@i" }, evento.Properties!.Keys);
    }

    // --- BLOCO E: @r ------------------------------------------------------------

    [Theory]
    [InlineData(@"""@r"":{""a"":1}")]
    [InlineData(@"""@r"":null")]
    [InlineData(@"""@r"":7")]
    public void A_renderings_field_that_is_not_an_array_invalidates_the_line(string trecho) =>
        // Repare que aqui null NÃO é tratado como ausente, ao contrário dos campos de texto.
        Invalida("{" + Instante + @",""@mt"":""x""," + trecho + "}");

    [Fact]
    public void A_rendering_replaces_the_formatted_value_in_the_message()
    {
        // 208 linhas reais trazem @r; ignorá-lo mudava a mensagem de 38 delas.
        var evento = Ler(@"{" + Instante + @",""@mt"":""v {V:000}"",""V"":7,""@r"":[""007""]}");

        Assert.Equal("v 007", evento.Message);
    }

    [Fact]
    public void Renderings_are_matched_positionally_with_the_formatted_tokens()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""{A:000} {B:000}"",""A"":1,""B"":2,""@r"":[""001""]}");

        // O Zip para no menor dos dois: o token que sobra volta a ser formatado normalmente.
        Assert.Equal("001 002", evento.Message);
    }

    [Fact]
    public void Extra_rendering_elements_are_ignored()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""{A:000}"",""A"":1,""@r"":[""001"",""999"",""xxx""]}");

        Assert.Equal("001", evento.Message);
    }

    [Fact]
    public void The_same_property_can_carry_two_different_formats()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""{N:000} {N:0.0}"",""N"":1,""@r"":[""001"",""1.0""]}");

        Assert.Equal("001 1.0", evento.Message);
    }

    [Fact]
    public void A_numeric_rendering_element_is_converted_to_text()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""{V:000}"",""V"":7,""@r"":[123]}");

        Assert.Equal("123", evento.Message);
    }

    [Fact]
    public void A_null_rendering_element_renders_as_nothing()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""[{V:000}]"",""V"":7,""@r"":[null]}");

        Assert.Equal("[]", evento.Message);
    }

    [Fact]
    public void An_object_in_a_paired_rendering_slot_invalidates_the_line() =>
        Invalida(@"{" + Instante + @",""@mt"":""{V:000}"",""V"":7,""@r"":[{""x"":1}]}");

    [Fact]
    public void An_object_in_an_unpaired_rendering_slot_is_ignored()
    {
        // O Zip do leitor antigo nem chega a converter os elementos que sobram.
        var evento = Ler(@"{" + Instante + @",""@mt"":""{V:000}"",""V"":7,""@r"":[""007"",{""x"":1}]}");

        Assert.Equal("007", evento.Message);
    }

    [Fact]
    public void A_rendering_for_a_missing_property_is_applied_to_nobody()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""{X:000}"",""V"":7,""@r"":[""007""]}");

        Assert.Equal("{X:000}", evento.Message);
    }

    [Fact]
    public void The_heartbeat_line_from_the_real_logs_keeps_the_pre_rendered_date()
    {
        // Linha real de C:\TOTVSPDV\Logs: sem aplicar o @r a data sairia entre aspas.
        var comR = Ler(@"{" + Instante + @",""@mt"":""[Heartbeat] '{Name}' atualizado em {Now:O}."",""Name"":""PDV"",""Now"":""2026-07-30T18:27:28.2984342Z"",""@r"":[""2026-07-30T18:27:28.2984342Z""]}");
        var semR = Ler(@"{" + Instante + @",""@mt"":""[Heartbeat] '{Name}' atualizado em {Now:O}."",""Name"":""PDV"",""Now"":""2026-07-30T18:27:28.2984342Z""}");

        Assert.Equal(@"[Heartbeat] '""PDV""' atualizado em 2026-07-30T18:27:28.2984342Z.", comR.Message);
        Assert.Equal(@"[Heartbeat] '""PDV""' atualizado em ""2026-07-30T18:27:28.2984342Z"".", semR.Message);
    }

    // --- BLOCO F: nomes de propriedade ------------------------------------------

    [Theory]
    [InlineData(@"""@@x""", "@x")]
    [InlineData(@"""@@@x""", "@@x")]
    [InlineData(@"""@foo""", "@foo")]
    [InlineData(@"""@T""", "@T")]
    [InlineData(@"""@MT""", "@MT")]
    [InlineData(@"""@@t""", "@t")]
    public void Property_names_lose_exactly_one_escaping_at(string nome, string esperado)
    {
        // "@T" não é reservado (a comparação é sensível a maiúsculas) e vira propriedade comum.
        var evento = Ler("{" + Instante + @",""@mt"":""x""," + nome + @":""literal""}");

        Assert.True(evento.Properties!.ContainsKey(esperado));
    }

    [Theory]
    [InlineData(@"""""")]
    [InlineData(@"""   """)]
    public void An_empty_property_name_becomes_unnamed(string nome)
    {
        // O formato permite nome vazio, o Serilog não representa — e um LogEventProperty com
        // nome vazio lançaria ArgumentException no meio da carga.
        var evento = Ler("{" + Instante + @",""@mt"":""x""," + nome + @":""sem nome""}");

        Assert.True(evento.Properties!.ContainsKey("(unnamed)"));
    }

    [Fact]
    public void A_repeated_property_keeps_the_last_value_at_the_first_position()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""A"":1,""B"":9,""A"":2}");

        Assert.Equal(new[] { "A", "B" }, evento.Properties!.Keys);
        Assert.Equal(2L, Valor(evento, "A"));
    }

    [Fact]
    public void The_insertion_order_of_the_properties_is_preserved()
    {
        // A grade descobre as colunas varrendo o dicionário na ordem em que ele foi montado.
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""Z"":1,""A"":2,""M"":3}");

        Assert.Equal(new[] { "Z", "A", "M" }, evento.Properties!.Keys);
    }

    [Fact]
    public void Names_that_differ_only_in_case_collide_in_the_event_dictionary()
    {
        // Divergência CONHECIDA e aceita: o Serilog guardaria "A" e "a" separados, mas o
        // dicionário do ClefEvent é OrdinalIgnoreCase desde sempre — o último valor vence.
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""A"":1,""a"":2}");

        Assert.Single(evento.Properties!);
        Assert.Equal(2L, Valor(evento, "A"));
    }

    [Fact]
    public void An_empty_name_inside_a_nested_object_also_becomes_unnamed()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""Obj"":{"""":1,""ok"":2}}");

        var estrutura = Assert.IsType<StructureValue>(evento.Properties!["Obj"]);
        Assert.Equal(new[] { "(unnamed)", "ok" }, estrutura.Properties.Select(p => p.Name));
    }

    // --- BLOCO G: números -------------------------------------------------------

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("-42", -42L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void An_integer_that_fits_in_long_stays_a_long(string literal, long esperado)
    {
        var evento = Ler("{" + Instante + @",""@mt"":""x"",""V"":" + literal + "}");

        Assert.Equal(esperado, Assert.IsType<long>(Valor(evento, "V")));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("18446744073709551615")]
    [InlineData("123456789012345678901234567890")]
    // 79228162514264337593543950335 é o decimal.MaxValue: o leitor antigo NÃO devolve decimal.
    [InlineData("79228162514264337593543950335")]
    public void An_integer_that_overflows_long_becomes_a_big_integer(string literal)
    {
        // Virar double mudaria o texto exibido de "9223372036854775808" para "9,22E+18".
        var evento = Ler("{" + Instante + @",""@mt"":""x"",""V"":" + literal + "}");

        var valor = Assert.IsType<BigInteger>(Valor(evento, "V"));
        Assert.Equal(literal, valor.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1.0", "1")]
    [InlineData("1.10", "1.1")]
    [InlineData("0.30000000000000004", "0.30000000000000004")]
    [InlineData("1e3", "1000")]
    [InlineData("1E3", "1000")]
    [InlineData("2.5E+3", "2500")]
    [InlineData("1.5e10", "15000000000")]
    [InlineData("1e-5", "1E-05")]
    [InlineData("-0.0", "-0")]
    [InlineData("0.1234567890123456789012345678", "0.12345678901234568")]
    [InlineData("1.7976931348623157E+308", "1.7976931348623157E+308")]
    public void Anything_with_a_dot_or_an_exponent_becomes_a_double(string literal, string renderizado)
    {
        // Decimal NUNCA sai do leitor antigo: "1.0" renderiza "1", e um decimal renderizaria "1.0".
        var evento = Ler("{" + Instante + @",""@mt"":""x"",""V"":" + literal + "}");

        Assert.IsType<double>(Valor(evento, "V"));
        Assert.Equal(renderizado, Texto(evento, "V"));
    }

    [Fact]
    public void Big_integers_survive_inside_arrays_and_nested_objects()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""L"":[9223372036854775808,1.5],""O"":{""g"":9223372036854775808}}");

        var lista = Assert.IsType<SequenceValue>(evento.Properties!["L"]);
        Assert.IsType<BigInteger>(Assert.IsType<ScalarValue>(lista.Elements[0]).Value);
        Assert.IsType<double>(Assert.IsType<ScalarValue>(lista.Elements[1]).Value);

        var objeto = Assert.IsType<StructureValue>(evento.Properties!["O"]);
        Assert.IsType<BigInteger>(Assert.IsType<ScalarValue>(objeto.Properties[0].Value).Value);
    }

    [Theory]
    [InlineData("007")]
    [InlineData("0x1F")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Number_syntax_that_json_forbids_invalidates_the_line(string literal) =>
        // Divergência CONHECIDA: o Newtonsoft aceitava esses literais. Nenhum formatter do
        // Serilog os produz e nenhuma das 314.973 linhas reais traz um.
        Invalida("{" + Instante + @",""@mt"":""x"",""V"":" + literal + "}");

    // --- BLOCO H: estruturas ----------------------------------------------------

    [Fact]
    public void An_object_with_a_type_tag_becomes_a_structure_without_the_tag_property()
    {
        // O enricher DeviceInfo põe $type em 314.973 de 314.973 linhas reais: caminho quente.
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":{""A"":1,""$type"":""MeuTipo""}}");

        var estrutura = Assert.IsType<StructureValue>(evento.Properties!["V"]);
        Assert.Equal("MeuTipo", estrutura.TypeTag);
        Assert.Equal(new[] { "A" }, estrutura.Properties.Select(p => p.Name));
    }

    [Fact]
    public void The_type_tag_is_only_recognized_under_the_exact_name()
    {
        // "$typeTag" NÃO é campo especial: continua propriedade comum, sem virar TypeTag.
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":{""$typeTag"":""TU"",""A"":1}}");

        var estrutura = Assert.IsType<StructureValue>(evento.Properties!["V"]);
        Assert.Null(estrutura.TypeTag);
        Assert.Equal(new[] { "$typeTag", "A" }, estrutura.Properties.Select(p => p.Name));
    }

    [Fact]
    public void A_non_string_type_tag_is_converted_to_text()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":{""$type"":123,""A"":1}}");

        Assert.Equal("123", Assert.IsType<StructureValue>(evento.Properties!["V"]).TypeTag);
    }

    [Fact]
    public void Empty_objects_and_arrays_are_preserved()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""O"":{},""A"":[]}");

        Assert.Empty(Assert.IsType<StructureValue>(evento.Properties!["O"]).Properties);
        Assert.Empty(Assert.IsType<SequenceValue>(evento.Properties!["A"]).Elements);
    }

    [Fact]
    public void A_heterogeneous_array_keeps_the_order_and_the_types()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":[1,""x"",null,{""A"":1},[2,3]]}");

        var itens = Assert.IsType<SequenceValue>(evento.Properties!["V"]).Elements;
        Assert.Equal(5, itens.Count);
        Assert.Equal(1L, Assert.IsType<ScalarValue>(itens[0]).Value);
        Assert.Equal("x", Assert.IsType<ScalarValue>(itens[1]).Value);
        Assert.Null(Assert.IsType<ScalarValue>(itens[2]).Value);
        Assert.IsType<StructureValue>(itens[3]);
        Assert.IsType<SequenceValue>(itens[4]);
    }

    [Fact]
    public void A_null_property_is_kept_instead_of_being_dropped()
    {
        // Ausente e nulo são coisas diferentes para a descoberta de colunas do app.
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":null}");

        Assert.True(evento.Properties!.ContainsKey("V"));
        Assert.Null(Valor(evento, "V"));
    }

    [Fact]
    public void Nesting_three_levels_deep_still_carries_the_type_tag()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":{""n1"":{""n2"":{""n3"":""fundo"",""$type"":""T3""}}}}");

        var n1 = Assert.IsType<StructureValue>(evento.Properties!["V"]);
        var n2 = Assert.IsType<StructureValue>(n1.Properties[0].Value);
        var n3 = Assert.IsType<StructureValue>(n2.Properties[0].Value);
        Assert.Equal("T3", n3.TypeTag);
        Assert.Equal("fundo", Assert.IsType<ScalarValue>(n3.Properties[0].Value).Value);
    }

    [Fact]
    public void Booleans_keep_their_type()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""A"":true,""B"":false}");

        Assert.True((bool)Valor(evento, "A")!);
        Assert.False((bool)Valor(evento, "B")!);
    }

    [Fact]
    public void A_repeated_key_inside_a_nested_object_does_not_duplicate_the_property()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":{""A"":1,""A"":2}}");

        var estrutura = Assert.IsType<StructureValue>(evento.Properties!["V"]);
        Assert.Single(estrutura.Properties);
        Assert.Equal(2L, Assert.IsType<ScalarValue>(estrutura.Properties[0].Value).Value);
    }

    // --- BLOCO I: texto, escapes e linhas quebradas -----------------------------

    [Fact]
    public void Json_escapes_are_decoded()
    {
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""V"":""linha1\nlinha2\ttab \""aspas\"" \u00e7""}");

        Assert.Equal("linha1\nlinha2\ttab \"aspas\" ç", Valor(evento, "V"));
    }

    [Fact]
    public void Surrogate_pairs_and_raw_utf8_survive()
    {
        // Emoji fora do BMP: lido dos BYTES, um recorte errado partiria o par surrogate.
        var evento = Ler(@"{" + Instante + @",""@mt"":""emoji \ud83d\udcca e direto 📊 com acentuação""}");

        Assert.Equal("emoji 📊 e direto 📊 com acentuação", evento.Message);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData(@"""apenas texto""")]
    [InlineData("42")]
    [InlineData("#$%")]
    [InlineData("isto não é CLEF")]
    public void A_document_that_is_not_a_clef_object_is_invalid(string linha) => Invalida(linha);

    [Theory]
    [InlineData(@"{""@t"":""2026-07-31T22:44:16.4504192-03:00"",""@mt"":""truncado""")]
    [InlineData(@"{""@t"":""2026-07-31T22:44:16.4504192-03:00"",""@mt"":""lixo depois""}extra")]
    [InlineData(@"{""@t"":""2026-07-31T22:44:16.4504192-03:00"",""@mt"":""virgula final"",}")]
    [InlineData(@"{'@t':'2026-07-31T22:44:16.4504192-03:00','@mt':'aspas simples'}")]
    public void Lenient_json_that_newtonsoft_used_to_accept_is_now_invalid(string linha) =>
        // Divergência CONHECIDA e desejada: são todos JSON inválido. O custo de reproduzir a
        // leniência seria escrever um parser tolerante à mão, jogando fora o ganho da troca.
        Invalida(linha);

    [Fact]
    public void A_very_long_line_is_parsed_whole()
    {
        // Stack trace grande em @x é comum (2.406 linhas reais têm @x).
        var grande = new string('X', 1_000_000);
        var evento = Ler(@"{" + Instante + @",""@mt"":""x"",""@x"":""" + grande + @"""}");

        Assert.Equal(1_000_000, evento.Exception!.Length);
    }

    [Fact]
    public void The_source_file_is_recorded_on_every_event()
    {
        Assert.True(LeitorClef.TentarLer(
            @"{" + Instante + @",""@mt"":""x""}", @"C:\logs\app.clef", new CacheDeTemplates(), out var evento, out _));

        Assert.Equal(@"C:\logs\app.clef", evento!.SourceFile);
    }

    // --- Cache de templates -----------------------------------------------------

    [Fact]
    public void The_template_cache_is_shared_by_the_load_and_returns_one_instance()
    {
        // O texto do template é o mesmo em toda linha do arquivo: sem compartilhar a instância
        // seriam 315 mil strings para meia dúzia de valores distintos.
        var pool = new PoolDeTextos();
        var cache = CacheDeTemplates.Para(pool);

        Assert.Same(cache, CacheDeTemplates.Para(pool));

        var linha = @"{" + Instante + @",""@mt"":""Oi {Nome}"",""Nome"":""mundo""}";
        LeitorClef.TentarLer(linha, "a.clef", cache, out var primeiro, out _);
        LeitorClef.TentarLer(linha, "b.clef", cache, out var segundo, out _);

        Assert.Same(primeiro!.MessageTemplate, segundo!.MessageTemplate);
    }

    [Fact]
    public void Different_pools_do_not_share_the_cache()
    {
        // O cache morre com a carga: templates de um log já fechado não podem ficar retidos.
        Assert.NotSame(CacheDeTemplates.Para(new PoolDeTextos()), CacheDeTemplates.Para(new PoolDeTextos()));
    }

    [Fact]
    public async Task The_cache_survives_parallel_readers()
    {
        // A carga lê os arquivos com Parallel.ForEachAsync compartilhando o mesmo cache; um
        // Dictionary comum aqui corrompe a tabela e some com eventos de forma intermitente.
        var cache = CacheDeTemplates.Para(new PoolDeTextos());
        var linhas = Enumerable.Range(0, 200)
            .Select(i => @"{" + Instante + @",""@mt"":""t" + (i % 20) + @" {N}"",""N"":" + i + "}")
            .ToArray();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            var lidos = 0;
            foreach (var linha in linhas)
            {
                if (LeitorClef.TentarLer(linha, "x.clef", cache, out _, out _)) lidos++;
            }

            return lidos;
        })));

        Assert.All(resultados, r => Assert.Equal(linhas.Length, r));
    }
}
