using System.Globalization;

namespace MonRem.Protocol.Panels;

/// <summary>
/// Condição de acendimento ("&gt; 0.5", "== 1", "!= 0"...). Mesma regra para painéis do app e do ESP32.
/// </summary>
public sealed record Comparison(string Op, double Threshold)
{
    private static readonly string[] Ops = [">=", "<=", "==", "!=", ">", "<"];

    public bool Test(double x) => Op switch
    {
        ">=" => x >= Threshold,
        "<=" => x <= Threshold,
        "==" => x == Threshold,
        "!=" => x != Threshold,
        ">" => x > Threshold,
        _ => x < Threshold,
    };

    public static bool TryParse(string text, out Comparison? result)
    {
        var t = text.Trim();
        foreach (var op in Ops)
        {
            if (!t.StartsWith(op, StringComparison.Ordinal)) continue;
            if (double.TryParse(t[op.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                result = new Comparison(op, v);
                return true;
            }
            break;
        }
        result = null;
        return false;
    }

    /// <summary>Sem condição: aceso se ≠ 0.</summary>
    public static bool IsLit(Comparison? when, double value) => when?.Test(value) ?? value != 0;
}

public static class ValueFormat
{
    /// <summary>
    /// Formata um valor do sim: formato numérico .NET ("00000", "0.00") ou composto ("HDG {0:000}").
    /// Formato inválido vira "ERR" em vez de derrubar quem chamou.
    /// </summary>
    public static string Format(string? format, double? number, string? text)
    {
        object arg = text is not null ? text : number ?? 0;
        if (string.IsNullOrEmpty(format)) return Convert.ToString(arg, CultureInfo.InvariantCulture) ?? "";
        try
        {
            if (format.Contains('{', StringComparison.Ordinal)) return string.Format(CultureInfo.InvariantCulture, format, arg);
            return arg is double d ? d.ToString(format, CultureInfo.InvariantCulture) : arg.ToString() ?? "";
        }
        catch (FormatException)
        {
            return "ERR";
        }
    }

    /// <summary>Escala linear de [in0, in1] para [out0, out1], limitada à faixa de saída.</summary>
    public static double Scale(double x, double in0, double in1, double out0, double out1)
    {
        if (in1 == in0) return out0;
        var t = Math.Clamp((x - in0) / (in1 - in0), 0, 1);
        return out0 + (t * (out1 - out0));
    }
}
