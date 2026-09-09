using System.Globalization;
using System.Numerics;

namespace ClefExplorer.Helpers;

/// <summary>Chave numérica exata, inclusive para inteiros maiores que Int64.</summary>
public sealed class NumeroLog : IComparable, IComparable<NumeroLog>, IEquatable<NumeroLog>
{
    private readonly BigInteger _numerador;
    private readonly BigInteger _denominador;
    private readonly string _texto;

    private NumeroLog(BigInteger numerador, BigInteger denominador, string texto)
    {
        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerador), denominador);
        _numerador = numerador / divisor;
        _denominador = denominador / divisor;
        _texto = texto;
    }

    public static NumeroLog? Criar(object? valor)
    {
        if (valor is NumeroLog numero) return numero;
        if (valor is not (string or byte or sbyte or short or ushort or int or uint or long or ulong
            or decimal or double or float or BigInteger)) return null;
        var texto = Convert.ToString(valor, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(texto) || texto.Length > 10_000) return null;
        var partes = texto.Split(['e', 'E']);
        if (partes.Length > 2) return null;
        var expoente = 0;
        if (partes.Length == 2 && (!int.TryParse(partes[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out expoente)
            || expoente is < -10000 or > 10000)) return null;
        var mantissa = partes[0].Replace(",", "", StringComparison.Ordinal);
        var ponto = mantissa.IndexOf('.');
        var escala = ponto < 0 ? 0 : mantissa.Length - ponto - 1;
        if (ponto >= 0) mantissa = mantissa.Remove(ponto, 1);
        if (!BigInteger.TryParse(mantissa, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var inteiro)) return null;
        escala -= expoente;
        return escala >= 0
            ? new(inteiro, BigInteger.Pow(10, escala), texto)
            : new(inteiro * BigInteger.Pow(10, -escala), BigInteger.One, texto);
    }

    public int CompareTo(NumeroLog? other) => other is null ? 1
        : (_numerador * other._denominador).CompareTo(other._numerador * _denominador);
    public int CompareTo(object? obj) => obj is null ? 1 : CompareTo(Criar(obj)
        ?? throw new ArgumentException("O valor não é numérico.", nameof(obj)));
    public bool Equals(NumeroLog? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is NumeroLog other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_numerador, _denominador);
    public override string ToString() => _texto;
}
