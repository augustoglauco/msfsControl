using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonRem.Host.Core.Sim;
using MonRem.Protocol.Panels;

namespace MonRem.Host.Core.Panels;

/// <summary>
/// Layouts de painéis do app, lidos de &lt;DataDirectory&gt;/layouts/*.json.
/// Recarregar troca tudo de uma vez (snapshot imutável): leitores nunca veem meio carregamento.
/// </summary>
public sealed class LayoutStore(ILogger<LayoutStore>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private volatile Snapshot _snapshot = new([]);

    /// <summary>Disparado depois de cada recarga.</summary>
    public event Action? Changed;

    public IReadOnlyList<LoadedLayout> Layouts => _snapshot.Layouts;

    public void Reload(string directory)
    {
        List<LoadedLayout> loaded = [];
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                var layout = TryLoad(path);
                if (layout is null) continue;
                if (loaded.Any(l => l.Id == layout.Id))
                {
                    _logger.LogWarning("Layout \"{Id}\" repetido em {Path}; ignorado", layout.Id, path);
                    continue;
                }
                loaded.Add(layout);
            }
        }
        _snapshot = new Snapshot(loaded);
        _logger.LogInformation("Layouts do app: {Count} carregados de {Dir}", loaded.Count, directory);
        Changed?.Invoke();
    }

    /// <summary>Carrega a partir de objetos já prontos (testes).</summary>
    public void Load(IEnumerable<LayoutFile> layouts)
    {
        _snapshot = new Snapshot([.. layouts.Select(l => Build(l, "(memória)"))]);
        Changed?.Invoke();
    }

    public LoadedLayout? Find(string layoutId) => _snapshot.Layouts.FirstOrDefault(l => l.Id == layoutId);

    /// <summary>Painel pelo id composto "layout/painel".</summary>
    public PanelModel? FindPanel(string qualifiedId)
    {
        var slash = qualifiedId.IndexOf('/');
        if (slash <= 0) return null;
        return Find(qualifiedId[..slash])?.File.Panels.FirstOrDefault(p => p.Id == qualifiedId[(slash + 1)..]);
    }

    /// <summary>
    /// Layout para a aeronave: o que tem regex e casa ganha de um genérico (sem regex); depois, a prioridade;
    /// depois, o id (para ser determinístico). Null se nenhum casar.
    /// </summary>
    public LoadedLayout? Match(AircraftInfo? aircraft)
    {
        var haystack = aircraft is null ? "" : $"{aircraft.Title}\n{aircraft.LiveryName}\n{aircraft.AtcModel}\n{aircraft.AtcType}";
        return _snapshot.Layouts
            .Select(l => (Layout: l, Score: l.Pattern is null ? 0 : (aircraft is not null && SafeMatch(l.Pattern, haystack) ? 1 : -1)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Layout.File.Priority)
            .ThenBy(x => x.Layout.Id, StringComparer.Ordinal)
            .Select(x => x.Layout)
            .FirstOrDefault();
    }

    private LoadedLayout? TryLoad(string path)
    {
        try
        {
            var file = PanelJson.ParseLayout(File.ReadAllText(path));
            return Build(file, path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Layout {Path} ignorado: {Message}", path, ex.Message);
            return null;
        }
    }

    private LoadedLayout Build(LayoutFile file, string source)
    {
        var id = string.IsNullOrWhiteSpace(file.Id) ? Path.GetFileNameWithoutExtension(source) : file.Id;
        file = file with { Id = id, Name = string.IsNullOrWhiteSpace(file.Name) ? id : file.Name };
        foreach (var problem in PanelJson.Validate(file))
            _logger.LogWarning("Layout \"{Id}\": {Problem}", id, problem);

        Regex? pattern = null;
        if (!string.IsNullOrEmpty(file.Aircraft))
        {
            try
            {
                pattern = new Regex(file.Aircraft, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                // Já registrado pelo Validate; layout fica sem regex = nunca casa por aeronave.
                pattern = new Regex("(?!)", RegexOptions.None, TimeSpan.FromMilliseconds(50));
            }
        }
        return new LoadedLayout(id, file, pattern, source);
    }

    private static bool SafeMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private sealed record Snapshot(List<LoadedLayout> Layouts);
}

public sealed record LoadedLayout(string Id, LayoutFile File, Regex? Pattern, string Source)
{
    public IEnumerable<string> QualifiedPanelIds => File.Panels.Where(p => !string.IsNullOrWhiteSpace(p.Id)).Select(p => $"{Id}/{p.Id}");
}
