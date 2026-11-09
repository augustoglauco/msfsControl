using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MonRem.Host.Core.Sim;

namespace MonRem.Host.Core.Devices;

/// <summary>
/// Perfil de um painel ESP32: liga cada elemento do descritor (pelo nome) ao sim.
/// Arquivos JSON em &lt;DataDirectory&gt;/profiles/*.json. Exemplo em docs/perfis-de-painel.md.
/// </summary>
public sealed record DeviceProfile
{
    public string Name { get; init; } = "";

    /// <summary>Modelo do painel (DeviceHello.model); "*" = qualquer.</summary>
    public string Model { get; init; } = "*";

    /// <summary>Só este dispositivo (DeviceHello.device_id); vazio = qualquer do modelo.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>Regex contra título, livery e ATC model da aeronave; vazio = qualquer.</summary>
    public string Aircraft { get; init; } = "";

    /// <summary>Desempate entre perfis igualmente específicos.</summary>
    public int Priority { get; init; }

    /// <summary>true = o host não mapeia; o firmware usa as mensagens Sim* (modo direto).</summary>
    public bool Direct { get; init; }

    /// <summary>Teto de atualização de saída por elemento (Hz).</summary>
    public uint OutputMaxHz { get; init; } = 30;

    public List<InputBinding> Inputs { get; init; } = [];
    public List<OutputBinding> Outputs { get; init; } = [];

    [JsonIgnore]
    public string SourcePath { get; init; } = "";
}

/// <summary>Ação no sim: disparar evento, escrever variável ou executar RPN.</summary>
public sealed record SimAction
{
    /// <summary>Evento a disparar: "K:...", "H:...", "B:...".</summary>
    public string? Fire { get; init; }

    public List<long> Args { get; init; } = [];

    /// <summary>Variável a escrever: "A:...", "L:...", "B:...".</summary>
    public string? Set { get; init; }

    /// <summary>Valor para <see cref="Set"/>: número ou texto.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>Código RPN (calculator code) via ponte WASM.</summary>
    public string? Rpn { get; init; }
}

public sealed record EncoderAcceleration
{
    /// <summary>Detentes por segundo a partir dos quais acelera.</summary>
    public double Rate { get; init; } = 15;

    public int Multiplier { get; init; } = 5;
}

public sealed record AnalogMapping
{
    public string Set { get; init; } = "";
    public double[] In { get; init; } = [0, 4095];
    public double[] Out { get; init; } = [0, 100];

    /// <summary>Variação mínima da leitura bruta para enviar de novo.</summary>
    public double Deadzone { get; init; } = 8;

    public bool Invert { get; init; }
}

public sealed record InputBinding
{
    public string Element { get; init; } = "";

    // BUTTON
    public SimAction? Press { get; init; }
    public SimAction? Release { get; init; }

    // TOGGLE
    public SimAction? On { get; init; }
    public SimAction? Off { get; init; }

    // SELECTOR
    public List<SimAction?> Positions { get; init; } = [];

    // ENCODER
    public SimAction? Inc { get; init; }
    public SimAction? Dec { get; init; }
    public EncoderAcceleration? Accel { get; init; }

    // ANALOG
    public AnalogMapping? Analog { get; init; }

    /// <summary>TOGGLE/SELECTOR: na conexão, leva o sim para a posição física da chave.</summary>
    public bool SyncOnConnect { get; init; } = true;
}

public sealed record OutputBinding
{
    public string Element { get; init; } = "";

    /// <summary>Variável do sim que alimenta a saída; ausente com <see cref="Value"/> = constante.</summary>
    public string? Source { get; init; }

    /// <summary>Constante (ex. brilho do backlight).</summary>
    public double? Value { get; init; }

    /// <summary>LED: condição, ex. "&gt; 0.5", "== 1", "!= 0". Ausente = acende se diferente de zero.</summary>
    public string? When { get; init; }

    /// <summary>SEGMENT/TEXT: formato .NET, ex. "00000", "{0:000}", "HDG {0:000}".</summary>
    public string? Format { get; init; }

    /// <summary>TEXT: linha do display.</summary>
    public uint Row { get; init; }

    /// <summary>SERVO/STEPPER/LED com brilho: escala linear [entrada] → [saída].</summary>
    public double[]? In { get; init; }
    public double[]? Out { get; init; }

    /// <summary>Variação mínima da fonte para atualizar (passa direto para o epsilon da assinatura).</summary>
    public double Epsilon { get; init; }
}

/// <summary>Carrega perfis e escolhe o mais específico para um dispositivo e aeronave.</summary>
public sealed class DeviceProfileStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();
    private List<DeviceProfile> _profiles = [];

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public IReadOnlyList<DeviceProfile> Profiles
    {
        get { lock (_lock) return _profiles; }
    }

    public static DeviceProfileStore FromDirectory(string directory)
    {
        var store = new DeviceProfileStore();
        store.Reload(directory);
        return store;
    }

    public static DeviceProfileStore FromProfiles(IEnumerable<DeviceProfile> profiles)
    {
        var store = new DeviceProfileStore();
        lock (store._lock) store._profiles = [.. profiles];
        return store;
    }

    public void Reload(string directory)
    {
        List<DeviceProfile> loaded = [];
        List<string> errors = [];
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order())
            {
                try
                {
                    var p = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), Json)
                            ?? throw new JsonException("arquivo vazio");
                    loaded.Add(p with { SourcePath = file, Name = string.IsNullOrEmpty(p.Name) ? Path.GetFileNameWithoutExtension(file) : p.Name });
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        lock (_lock) _profiles = loaded;
        Errors = errors;
    }

    /// <summary>
    /// Perfil mais específico: device_id (+8) &gt; aeronave (+4) &gt; modelo exato (+2); depois priority.
    /// </summary>
    public DeviceProfile? Select(string model, string deviceId, AircraftInfo? aircraft)
    {
        DeviceProfile? best = null;
        var bestScore = int.MinValue;
        foreach (var p in Profiles)
        {
            if (p.Model != "*" && !string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.DeviceId.Length > 0 && !string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Aircraft.Length > 0 && !MatchesAircraft(p.Aircraft, aircraft)) continue;

            var score = (p.DeviceId.Length > 0 ? 8 : 0) + (p.Aircraft.Length > 0 ? 4 : 0) + (p.Model != "*" ? 2 : 0);
            score = score * 1000 + p.Priority;
            if (score > bestScore)
            {
                best = p;
                bestScore = score;
            }
        }
        return best;
    }

    private static bool MatchesAircraft(string pattern, AircraftInfo? aircraft)
    {
        if (aircraft is null) return false;
        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return regex.IsMatch(aircraft.Title) || regex.IsMatch(aircraft.LiveryName) || regex.IsMatch(aircraft.AtcModel);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
