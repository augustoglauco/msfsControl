using System.Globalization;

namespace MonRem.Host.Core.Sim;

public enum SimRefKind
{
    /// <summary>A: SimVar do SimConnect.</summary>
    A,
    /// <summary>L: variável local.</summary>
    L,
    /// <summary>B: Input Event do MSFS 2024.</summary>
    B,
    /// <summary>K: key event.</summary>
    K,
    /// <summary>H: evento HTML/WASM.</summary>
    H,
    /// <summary>RPN: calculator code.</summary>
    Rpn,
}

/// <summary>
/// Referência textual do protocolo, já validada.
/// Formatos: "A:NOME[:índice][,unidade]", "L:NOME[,unidade]", "B:NOME", "K:NOME", "H:NOME", "RPN:código".
/// </summary>
public sealed record SimRef(SimRefKind Kind, string Name, int? Index, string? Unit, string Raw)
{
    /// <summary>Chave de deduplicação entre clientes (mesma variável + unidade = mesma assinatura).</summary>
    public string Key => Kind switch
    {
        SimRefKind.A => $"A:{Name.ToUpperInvariant()}:{Index}:{Unit?.ToLowerInvariant()}",
        SimRefKind.L => $"L:{Name}:{Unit?.ToLowerInvariant()}",
        SimRefKind.Rpn => $"RPN:{Name}",
        _ => $"{Kind}:{Name}",
    };

    /// <summary>Nome no formato do SimConnect, com índice: "GENERAL ENG RPM:1".</summary>
    public string SimConnectName => Index is { } i ? $"{Name}:{i.ToString(CultureInfo.InvariantCulture)}" : Name;

    public bool IsReadable => Kind is SimRefKind.A or SimRefKind.L or SimRefKind.B or SimRefKind.Rpn;
    public bool IsSettable => Kind is SimRefKind.A or SimRefKind.L or SimRefKind.B;
    public bool IsFireable => Kind is SimRefKind.K or SimRefKind.H or SimRefKind.Rpn or SimRefKind.B;

    public static SimRef Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var colon = raw.IndexOf(':');
        if (colon <= 0) throw new FormatException($"referência sem prefixo: \"{raw}\"");

        var prefix = raw[..colon].ToUpperInvariant();
        var body = raw[(colon + 1)..];

        if (prefix == "RPN")
        {
            if (string.IsNullOrWhiteSpace(body)) throw new FormatException("RPN vazio");
            return new SimRef(SimRefKind.Rpn, body, null, null, raw);
        }

        if (string.IsNullOrWhiteSpace(body)) throw new FormatException($"referência sem nome: \"{raw}\"");

        return prefix switch
        {
            "A" => ParseA(body, raw),
            "L" => ParseWithUnit(SimRefKind.L, body, raw),
            "B" => Simple(SimRefKind.B, body, raw),
            "K" => Simple(SimRefKind.K, body, raw),
            "H" => Simple(SimRefKind.H, body, raw),
            _ => throw new FormatException($"prefixo desconhecido \"{prefix}\" em \"{raw}\""),
        };
    }

    public static bool TryParse(string raw, out SimRef? result, out string? error)
    {
        try
        {
            result = Parse(raw);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            result = null;
            error = ex.Message;
            return false;
        }
    }

    private static SimRef ParseA(string body, string raw)
    {
        var (nameAndIndex, unit) = SplitUnit(body);
        int? index = null;
        var name = nameAndIndex;
        var idx = nameAndIndex.LastIndexOf(':');
        if (idx >= 0)
        {
            var indexText = nameAndIndex[(idx + 1)..].Trim();
            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i < 1)
                throw new FormatException($"índice inválido \"{indexText}\" em \"{raw}\" (começa em 1)");
            index = i;
            name = nameAndIndex[..idx];
        }
        name = name.Trim();
        if (name.Length == 0) throw new FormatException($"nome vazio em \"{raw}\"");
        return new SimRef(SimRefKind.A, name, index, unit, raw);
    }

    private static SimRef ParseWithUnit(SimRefKind kind, string body, string raw)
    {
        var (name, unit) = SplitUnit(body);
        name = name.Trim();
        if (name.Length == 0) throw new FormatException($"nome vazio em \"{raw}\"");
        return new SimRef(kind, name, null, unit, raw);
    }

    private static SimRef Simple(SimRefKind kind, string body, string raw)
    {
        var name = body.Trim();
        if (name.Contains(',')) throw new FormatException($"{kind}: não aceita unidade (\"{raw}\")");
        return new SimRef(kind, name, null, null, raw);
    }

    private static (string Name, string? Unit) SplitUnit(string body)
    {
        var comma = body.IndexOf(',');
        if (comma < 0) return (body, null);
        var unit = body[(comma + 1)..].Trim();
        return (body[..comma], unit.Length == 0 ? null : unit);
    }
}
