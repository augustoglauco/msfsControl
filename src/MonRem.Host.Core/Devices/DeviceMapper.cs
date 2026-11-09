using System.Globalization;
using System.Text.Json;
using MonRem.Host.Core.Sim;
using MonRem.Protocol.V1;

namespace MonRem.Host.Core.Devices;

/// <summary>Ação já validada para o sim.</summary>
public sealed record DeviceAction(SimOperation Operation, string Ref, IReadOnlyList<long> Args, SimDatum Value)
{
    public static DeviceAction FireEvent(string reference, IReadOnlyList<long>? args = null) => new(SimOperation.Fire, reference, args ?? [], default);
    public static DeviceAction SetVar(string reference, SimDatum value) => new(SimOperation.Set, reference, [], value);
}

/// <summary>
/// Traduz o estado absoluto do painel em ações no sim, e valores do sim em estado de saída do painel,
/// segundo um <see cref="DeviceProfile"/>. Sem I/O: quem chama executa as ações e envia as saídas.
/// </summary>
public sealed class DeviceMapper
{
    private const int MaxRepeat = 100;

    private readonly Dictionary<uint, InputRule> _inputs = [];
    private readonly List<OutputRule> _outputs = [];
    private readonly Dictionary<string, OutputValue> _lastOutputs = [];
    private readonly List<string> _warnings = [];

    public DeviceMapper(DeviceProfile profile, IEnumerable<Element> elements)
    {
        Profile = profile;
        var byName = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in elements) byName.TryAdd(e.Name, e);

        foreach (var b in profile.Inputs)
        {
            if (!byName.TryGetValue(b.Element, out var element))
            {
                _warnings.Add($"entrada \"{b.Element}\": elemento não existe no painel");
                continue;
            }
            if (!IsInput(element.Kind))
            {
                _warnings.Add($"entrada \"{b.Element}\": elemento é uma saída ({element.Kind})");
                continue;
            }
            _inputs[element.Id] = new InputRule(element, b, Resolve(b.Press, b.Element), Resolve(b.Release, b.Element),
                Resolve(b.On, b.Element), Resolve(b.Off, b.Element), [.. b.Positions.Select(p => Resolve(p, b.Element))],
                Resolve(b.Inc, b.Element), Resolve(b.Dec, b.Element), ResolveAnalog(b.Analog, b.Element));
        }

        foreach (var b in profile.Outputs)
        {
            if (!byName.TryGetValue(b.Element, out var element))
            {
                _warnings.Add($"saída \"{b.Element}\": elemento não existe no painel");
                continue;
            }
            if (IsInput(element.Kind))
            {
                _warnings.Add($"saída \"{b.Element}\": elemento é uma entrada ({element.Kind})");
                continue;
            }
            if (b.Source is null && b.Value is null)
            {
                _warnings.Add($"saída \"{b.Element}\": sem source nem value");
                continue;
            }
            if (b.Source is not null && !SimRef.TryParse(b.Source, out _, out var err))
            {
                _warnings.Add($"saída \"{b.Element}\": {err}");
                continue;
            }
            Comparison? when = null;
            if (b.When is not null && !Comparison.TryParse(b.When, out when))
            {
                _warnings.Add($"saída \"{b.Element}\": condição inválida \"{b.When}\"");
                continue;
            }
            _outputs.Add(new OutputRule(element, b, when));
        }
    }

    public DeviceProfile Profile { get; }

    /// <summary>Problemas do perfil contra o descritor (elementos inexistentes, ações inválidas).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Variáveis do sim que alimentam saídas, sem repetição, com o menor epsilon pedido.</summary>
    public IReadOnlyList<SimVarSpec> OutputSources =>
        [.. _outputs.Where(o => o.Binding.Source is not null)
            .GroupBy(o => o.Binding.Source!, StringComparer.Ordinal)
            .Select(g => new SimVarSpec { Ref = g.Key, Epsilon = g.Min(o => o.Binding.Epsilon) })];

    // ------------------------------------------------------------------ entradas

    /// <summary>
    /// Processa um lote de estado de entrada. <paramref name="tDeviceMs"/> é o relógio do painel (para aceleração do encoder).
    /// </summary>
    public IReadOnlyList<DeviceAction> OnInput(IEnumerable<InputValue> items, uint tDeviceMs)
    {
        List<DeviceAction> actions = [];
        foreach (var v in items)
        {
            if (_inputs.TryGetValue(v.Id, out var rule)) Apply(rule, v, tDeviceMs, actions);
        }
        return actions;
    }

    private static void Apply(InputRule r, InputValue v, uint t, List<DeviceAction> actions)
    {
        switch (r.Element.Kind)
        {
            case ElementKind.Button when v.ValueCase == InputValue.ValueOneofCase.Pressed:
                if (r.LastBool is { } prevButton && prevButton != v.Pressed)
                    Add(actions, v.Pressed ? r.Press : r.Release);
                r.LastBool = v.Pressed;
                break;

            case ElementKind.Toggle when v.ValueCase == InputValue.ValueOneofCase.Pressed:
                if (r.LastBool != v.Pressed && (r.LastBool is not null || r.Binding.SyncOnConnect))
                    Add(actions, v.Pressed ? r.On : r.Off);
                r.LastBool = v.Pressed;
                break;

            case ElementKind.Selector when v.ValueCase == InputValue.ValueOneofCase.Position:
                if (r.LastPosition != v.Position && (r.LastPosition is not null || r.Binding.SyncOnConnect) && v.Position < r.Positions.Length)
                    Add(actions, r.Positions[v.Position]);
                r.LastPosition = v.Position;
                break;

            case ElementKind.Encoder when v.ValueCase == InputValue.ValueOneofCase.Count:
                ApplyEncoder(r, v.Count, t, actions);
                break;

            case ElementKind.Analog when v.ValueCase == InputValue.ValueOneofCase.Analog && r.Analog is { } a:
                if (r.LastAnalogSent is { } sent && Math.Abs(v.Analog - sent) <= a.Deadzone) break;
                r.LastAnalogSent = v.Analog;
                actions.Add(DeviceAction.SetVar(a.Set, SimDatum.Of(Scale(v.Analog, a.In, a.Out, a.Invert))));
                break;
        }
    }

    private static void ApplyEncoder(InputRule r, int count, uint t, List<DeviceAction> actions)
    {
        if (r.LastCount is not { } last)
        {
            r.LastCount = count; // primeira leitura: só a referência
            r.LastStepTime = t;
            return;
        }

        var delta = unchecked(count - last); // contador acumulado com wrap em int32
        r.LastCount = count;
        if (delta == 0) return;

        var detents = Math.Max(1, (int)r.Element.Detents);
        var total = r.PulseRemainder + delta;
        var steps = total / detents;
        r.PulseRemainder = total % detents;
        if (steps == 0) return;

        if (r.Binding.Accel is { } accel)
        {
            var dtMs = Math.Max(1u, unchecked(t - r.LastStepTime ?? t));
            var rate = Math.Abs(steps) * 1000.0 / dtMs;
            if (rate >= accel.Rate) steps *= Math.Max(1, accel.Multiplier);
        }
        r.LastStepTime = t;

        var action = steps > 0 ? r.Inc : r.Dec;
        if (action is null) return;
        var repeat = Math.Min(Math.Abs(steps), MaxRepeat);
        for (var i = 0; i < repeat; i++) actions.Add(action);
    }

    private static void Add(List<DeviceAction> actions, DeviceAction? action)
    {
        if (action is not null) actions.Add(action);
    }

    // ------------------------------------------------------------------ saídas

    /// <summary>Novo valor de uma variável-fonte. Retorna só as saídas que mudaram.</summary>
    public IReadOnlyList<OutputValue> OnSourceValue(string source, SimDatum value)
    {
        List<OutputValue> changed = [];
        foreach (var o in _outputs)
        {
            if (!string.Equals(o.Binding.Source, source, StringComparison.Ordinal)) continue;
            o.Last = value;
            if (Compute(o, value) is { } output && Remember(o, output)) changed.Add(output);
        }
        return changed;
    }

    /// <summary>Todas as saídas com valor conhecido (constantes incluídas): para o estado completo.</summary>
    public IReadOnlyList<OutputValue> FullOutputs()
    {
        List<OutputValue> all = [];
        foreach (var o in _outputs)
        {
            var datum = o.Binding.Source is null ? SimDatum.Of(o.Binding.Value!.Value) : o.Last;
            if (datum is not { } d || Compute(o, d) is not { } output) continue;
            Remember(o, output);
            all.Add(output);
        }
        return all;
    }

    private bool Remember(OutputRule o, OutputValue output)
    {
        var key = $"{o.Element.Id}:{output.TextRow}";
        if (_lastOutputs.TryGetValue(key, out var previous) && previous.Equals(output)) return false;
        _lastOutputs[key] = output;
        return true;
    }

    private static OutputValue? Compute(OutputRule o, SimDatum v)
    {
        var b = o.Binding;
        var e = o.Element;
        var number = v.Number ?? (double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN);
        var output = new OutputValue { Id = e.Id };

        switch (e.Kind)
        {
            case ElementKind.Led when b.In is not null && b.Out is not null:
            case ElementKind.Backlight:
            case ElementKind.Servo:
                if (double.IsNaN(number)) return null;
                var scaled = b.In is not null && b.Out is not null ? Scale(number, b.In, b.Out, false) : number;
                output.Level = (uint)Math.Clamp(Math.Round(scaled), 0, uint.MaxValue);
                return output;

            case ElementKind.Led:
                if (double.IsNaN(number)) return null;
                output.On = o.When?.Test(number) ?? number != 0;
                return output;

            case ElementKind.Stepper:
                if (double.IsNaN(number)) return null;
                var target = b.In is not null && b.Out is not null ? Scale(number, b.In, b.Out, false) : number;
                output.Target = (int)Math.Clamp(Math.Round(target), int.MinValue, int.MaxValue);
                return output;

            case ElementKind.Segment:
            case ElementKind.Text:
                var text = Format(b.Format, v);
                if (e.Kind == ElementKind.Text)
                {
                    if (e.Cols > 0 && text.Length > e.Cols) text = text[..(int)e.Cols];
                    output.TextRow = b.Row;
                }
                output.Text = text;
                return output;

            default:
                return null;
        }
    }

    private static string Format(string? format, SimDatum v)
    {
        object arg = v.Text is not null ? v.Text : v.Number ?? 0;
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

    // ------------------------------------------------------------------ utilidades

    internal static double Scale(double x, double[] input, double[] output, bool invert)
    {
        if (input.Length < 2 || output.Length < 2 || input[1] == input[0]) return x;
        var t = Math.Clamp((x - input[0]) / (input[1] - input[0]), 0, 1);
        if (invert) t = 1 - t;
        return output[0] + t * (output[1] - output[0]);
    }

    private static bool IsInput(ElementKind kind) =>
        kind is ElementKind.Button or ElementKind.Toggle or ElementKind.Selector or ElementKind.Encoder or ElementKind.Analog;

    private DeviceAction? Resolve(SimAction? a, string element)
    {
        if (a is null) return null;
        var count = (a.Fire is not null ? 1 : 0) + (a.Set is not null ? 1 : 0) + (a.Rpn is not null ? 1 : 0);
        if (count != 1)
        {
            _warnings.Add($"\"{element}\": cada ação precisa de exatamente um entre fire, set e rpn");
            return null;
        }

        var reference = a.Fire ?? a.Set ?? $"RPN:{a.Rpn}";
        if (!SimRef.TryParse(reference, out var parsed, out var err))
        {
            _warnings.Add($"\"{element}\": {err}");
            return null;
        }

        if (a.Set is not null)
        {
            if (!parsed!.IsSettable)
            {
                _warnings.Add($"\"{element}\": {a.Set} não pode ser escrita");
                return null;
            }
            SimDatum? value = a.Value switch
            {
                { ValueKind: JsonValueKind.Number } n => SimDatum.Of(n.GetDouble()),
                { ValueKind: JsonValueKind.String } s => SimDatum.Of(s.GetString()!),
                { ValueKind: JsonValueKind.True } => SimDatum.Of(1),
                { ValueKind: JsonValueKind.False } => SimDatum.Of(0),
                _ => null,
            };
            if (value is null)
            {
                _warnings.Add($"\"{element}\": set sem value");
                return null;
            }
            return DeviceAction.SetVar(reference, value.Value);
        }

        if (!parsed!.IsFireable)
        {
            _warnings.Add($"\"{element}\": {reference} não é um evento");
            return null;
        }
        if (a.Args.Count > 5)
        {
            _warnings.Add($"\"{element}\": no máximo 5 args");
            return null;
        }
        return DeviceAction.FireEvent(reference, a.Args);
    }

    private AnalogMapping? ResolveAnalog(AnalogMapping? a, string element)
    {
        if (a is null) return null;
        if (!SimRef.TryParse(a.Set, out var r, out var err) || !r!.IsSettable)
        {
            _warnings.Add($"\"{element}\": analog.set inválido ({err ?? a.Set})");
            return null;
        }
        return a;
    }

    private sealed class InputRule(
        Element element, InputBinding binding,
        DeviceAction? press, DeviceAction? release, DeviceAction? on, DeviceAction? off,
        DeviceAction?[] positions, DeviceAction? inc, DeviceAction? dec, AnalogMapping? analog)
    {
        public Element Element { get; } = element;
        public InputBinding Binding { get; } = binding;
        public DeviceAction? Press { get; } = press;
        public DeviceAction? Release { get; } = release;
        public DeviceAction? On { get; } = on;
        public DeviceAction? Off { get; } = off;
        public DeviceAction?[] Positions { get; } = positions;
        public DeviceAction? Inc { get; } = inc;
        public DeviceAction? Dec { get; } = dec;
        public AnalogMapping? Analog { get; } = analog;

        public bool? LastBool { get; set; }
        public uint? LastPosition { get; set; }
        public int? LastCount { get; set; }
        public int PulseRemainder { get; set; }
        public uint? LastStepTime { get; set; }
        public uint? LastAnalogSent { get; set; }
    }

    private sealed class OutputRule(Element element, OutputBinding binding, Comparison? when)
    {
        public Element Element { get; } = element;
        public OutputBinding Binding { get; } = binding;
        public Comparison? When { get; } = when;
        public SimDatum? Last { get; set; }
    }

    private sealed record Comparison(string Op, double Threshold)
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
    }
}
