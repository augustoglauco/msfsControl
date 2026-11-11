using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MonRem.Protocol.Panels;

/// <summary>
/// Arquivo de layout no host (pasta layouts/*.json): um conjunto de painéis do app para uma aeronave.
/// </summary>
public sealed record LayoutFile
{
    /// <summary>Id estável; vazio = nome do arquivo sem extensão.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Regex contra título, livery e ATC model da aeronave. Vazio = qualquer aeronave.</summary>
    public string Aircraft { get; init; } = "";

    /// <summary>Desempate entre layouts que casam com a mesma aeronave.</summary>
    public int Priority { get; init; }

    public List<PanelModel> Panels { get; init; } = [];
}

/// <summary>
/// Painel do app (schema 1): área lógica <see cref="Width"/>×<see cref="Height"/> escalada para a tela,
/// com widgets posicionados em coordenadas lógicas.
/// </summary>
public sealed record PanelModel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double Width { get; init; } = 1280;
    public double Height { get; init; } = 800;
    public string? Background { get; init; }
    public List<WidgetModel> Widgets { get; init; } = [];
}

public static class WidgetTypes
{
    public const string Button = "button";
    public const string Toggle = "toggle";
    public const string Encoder = "encoder";
    public const string Selector = "selector";
    public const string Display = "display";
    public const string Gauge = "gauge";
    public const string Annunciator = "annunciator";
    public const string Label = "label";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Button, Toggle, Encoder, Selector, Display, Gauge, Annunciator, Label,
    };
}

public sealed record WidgetModel
{
    public string Type { get; init; } = "";
    public string Id { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; } = 120;
    public double H { get; init; } = 80;
    public string? Label { get; init; }

    /// <summary>Variável lida do sim (estado do toggle, valor do display, luz do botão...).</summary>
    public string? Source { get; init; }

    /// <summary>Condição de "aceso" sobre <see cref="Source"/> (padrão: ≠ 0).</summary>
    public string? When { get; init; }

    public string? Format { get; init; }
    public double Epsilon { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public string? Unit { get; init; }
    public string? Color { get; init; }
    public double? FontSize { get; init; }

    public PanelAction? Press { get; init; }

    /// <summary>Botão: ação no toque quando ele está aceso (ex.: AUTOPILOT_OFF). Sem ela, usa <see cref="Press"/>.</summary>
    public PanelAction? PressOff { get; init; }
    public PanelAction? Release { get; init; }
    public PanelAction? On { get; init; }
    public PanelAction? Off { get; init; }
    public PanelAction? Inc { get; init; }
    public PanelAction? Dec { get; init; }

    /// <summary>Toque no centro do encoder (ex.: push/pull do FCU).</summary>
    public PanelAction? Push { get; init; }

    public List<SelectorPosition> Positions { get; init; } = [];
}

/// <summary>Ação: exatamente um de fire, set ou rpn (mesma regra dos perfis ESP32).</summary>
public sealed record PanelAction
{
    public string? Fire { get; init; }
    public List<long> Args { get; init; } = [];
    public string? Set { get; init; }
    public JsonElement? Value { get; init; }
    public string? Rpn { get; init; }
}

public sealed record SelectorPosition
{
    public string Label { get; init; } = "";

    /// <summary>Valor de <see cref="WidgetModel.Source"/> que indica esta posição.</summary>
    public double? Value { get; init; }

    public PanelAction? Action { get; init; }
}

public static class PanelJson
{
    public const uint Schema = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static LayoutFile ParseLayout(string json) =>
        JsonSerializer.Deserialize<LayoutFile>(json, Options) ?? throw new JsonException("layout vazio");

    public static PanelModel ParsePanel(string json) =>
        JsonSerializer.Deserialize<PanelModel>(json, Options) ?? throw new JsonException("painel vazio");

    public static string Serialize(PanelModel panel) => JsonSerializer.Serialize(panel, Options);

    /// <summary>Problemas do layout (não impedem o carregamento; o widget com problema é ignorado pelo app).</summary>
    public static List<string> Validate(LayoutFile layout)
    {
        List<string> problems = [];
        if (!string.IsNullOrEmpty(layout.Aircraft))
        {
            try
            {
                _ = new Regex(layout.Aircraft, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                problems.Add($"aircraft: regex inválida ({ex.Message})");
            }
        }

        var panelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in layout.Panels)
        {
            if (string.IsNullOrWhiteSpace(panel.Id)) problems.Add("painel sem id");
            else if (!panelIds.Add(panel.Id)) problems.Add($"painel \"{panel.Id}\" repetido");
            else if (panel.Id.Contains('/', StringComparison.Ordinal)) problems.Add($"painel \"{panel.Id}\": id não pode ter '/'");
            if (panel.Width <= 0 || panel.Height <= 0) problems.Add($"painel \"{panel.Id}\": largura e altura devem ser > 0");

            foreach (var w in panel.Widgets) problems.AddRange(ValidateWidget(panel, w).Select(p => $"painel \"{panel.Id}\", {Describe(w)}: {p}"));
        }
        return problems;
    }

    public static IEnumerable<string> ValidateWidget(PanelModel panel, WidgetModel w)
    {
        if (!WidgetTypes.All.Contains(w.Type))
        {
            yield return $"tipo desconhecido \"{w.Type}\"";
            yield break;
        }
        if (w.W <= 0 || w.H <= 0) yield return "w e h devem ser > 0";
        if (w.X < 0 || w.Y < 0 || w.X + w.W > panel.Width + 0.5 || w.Y + w.H > panel.Height + 0.5)
            yield return "fora da área do painel";
        if (w.When is not null && !Comparison.TryParse(w.When, out _)) yield return $"condição inválida \"{w.When}\"";
        if (w.Source is not null && !LooksReadable(w.Source)) yield return $"source \"{w.Source}\" não é legível (use A:, L:, B: ou RPN:)";

        var needsSource = w.Type is WidgetTypes.Display or WidgetTypes.Gauge or WidgetTypes.Annunciator;
        if (needsSource && w.Source is null) yield return "precisa de source";
        if (w.Type == WidgetTypes.Gauge && w.Max <= w.Min) yield return "max deve ser maior que min";

        IEnumerable<(string Name, PanelAction? Action)> actions = w.Type switch
        {
            WidgetTypes.Button => [("press", w.Press), ("press_off", w.PressOff), ("release", w.Release)],
            WidgetTypes.Toggle => [("on", w.On), ("off", w.Off)],
            WidgetTypes.Encoder => [("inc", w.Inc), ("dec", w.Dec), ("push", w.Push)],
            _ => [],
        };
        foreach (var (name, action) in actions)
        {
            if (action is null) continue;
            var problem = ValidateAction(action);
            if (problem is not null) yield return $"{name}: {problem}";
        }
        if (w.Type == WidgetTypes.Button && w.Press is null) yield return "botão sem press";
        if (w.Type == WidgetTypes.Button && w.PressOff is not null && w.Source is null) yield return "press_off precisa de source (para saber se está aceso)";
        if (w.Type == WidgetTypes.Toggle && (w.On is null || w.Off is null)) yield return "toggle precisa de on e off";
        if (w.Type == WidgetTypes.Encoder && (w.Inc is null || w.Dec is null)) yield return "encoder precisa de inc e dec";
        if (w.Type == WidgetTypes.Selector)
        {
            if (w.Positions.Count < 2) yield return "seletor precisa de 2 ou mais posições";
            for (var i = 0; i < w.Positions.Count; i++)
            {
                var p = w.Positions[i].Action is null ? "sem action" : ValidateAction(w.Positions[i].Action!);
                if (p is not null) yield return $"posição {i}: {p}";
            }
        }
    }

    public static string? ValidateAction(PanelAction a)
    {
        var count = (a.Fire is null ? 0 : 1) + (a.Set is null ? 0 : 1) + (a.Rpn is null ? 0 : 1);
        if (count != 1) return "use exatamente um de fire, set ou rpn";
        if (a.Fire is not null)
        {
            var prefix = Prefix(a.Fire);
            if (prefix is not ("K" or "H" or "B")) return $"fire aceita K:, H: ou B: (\"{a.Fire}\")";
            if (a.Args.Count > 5) return "no máximo 5 args";
        }
        if (a.Set is not null)
        {
            if (Prefix(a.Set) is not ("A" or "L" or "B")) return $"set aceita A:, L: ou B: (\"{a.Set}\")";
            if (a.Value is null) return "set precisa de value";
            if (a.Value.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) return "value deve ser número ou texto";
        }
        if (a.Rpn is not null && string.IsNullOrWhiteSpace(a.Rpn)) return "rpn vazio";
        return null;
    }

    private static bool LooksReadable(string reference) => Prefix(reference) is "A" or "L" or "B" or "RPN";

    private static string Prefix(string reference)
    {
        var colon = reference.IndexOf(':');
        return colon <= 0 ? "" : reference[..colon].ToUpperInvariant();
    }

    private static string Describe(WidgetModel w) =>
        string.IsNullOrEmpty(w.Id) ? $"{w.Type} em ({w.X.ToString(CultureInfo.InvariantCulture)}, {w.Y.ToString(CultureInfo.InvariantCulture)})" : $"{w.Type} \"{w.Id}\"";
}
