namespace ClefExplorer.Helpers
{
    /// <summary>Formatação compacta de intervalos usados nos diagnósticos temporais.</summary>
    public static class FormatacaoTempo
    {
        public static string Intervalo(TimeSpan intervalo)
        {
            if (intervalo < TimeSpan.Zero) intervalo = intervalo.Negate();

            if (intervalo.TotalDays >= 1)
                return intervalo.Hours == 0
                    ? $"{(int)intervalo.TotalDays} d"
                    : $"{(int)intervalo.TotalDays} d {intervalo.Hours} h";
            if (intervalo.TotalHours >= 1)
                return intervalo.Minutes == 0
                    ? $"{(int)intervalo.TotalHours} h"
                    : $"{(int)intervalo.TotalHours} h {intervalo.Minutes} min";
            if (intervalo.TotalMinutes >= 1)
                return intervalo.Seconds == 0
                    ? $"{(int)intervalo.TotalMinutes} min"
                    : $"{(int)intervalo.TotalMinutes} min {intervalo.Seconds} s";
            if (intervalo.TotalSeconds >= 1)
                return $"{intervalo.TotalSeconds:0.###} s";
            if (intervalo.TotalMilliseconds >= 1)
                return $"{intervalo.TotalMilliseconds:0.###} ms";
            if (intervalo.Ticks >= 10)
                return $"{intervalo.Ticks / 10d:0.###} µs";
            if (intervalo > TimeSpan.Zero)
                return "< 1 µs";
            return "0 ms";
        }
    }
}
