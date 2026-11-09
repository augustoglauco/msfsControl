using MonRem.Protocol;

namespace MonRem.Host.Core;

public sealed class MonRemHostOptions
{
    public const string Section = "MonRem";

    /// <summary>Pasta de dados: certificado do host, clientes pareados.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MonRem");

    /// <summary>Nome exibido aos clientes.</summary>
    public string HostName { get; set; } = Environment.MachineName;

    public int ControlPort { get; set; } = ProtocolConstants.DefaultControlPort;
    public int MediaPort { get; set; } = ProtocolConstants.DefaultMediaPort;
    public int InputPort { get; set; } = ProtocolConstants.DefaultInputPort;

    /// <summary>Usa o simulador falso em vez do MSFS (desenvolvimento sem o sim aberto).</summary>
    public bool UseFakeSim { get; set; }

    /// <summary>
    /// Aceita novos pareamentos. Quando houver a interface da bandeja, o padrão passa a ser
    /// "desligado" e o usuário liga o modo de pareamento só quando for parear.
    /// </summary>
    public bool AllowPairing { get; set; } = true;

    /// <summary>Iterações do PBKDF2 do PIN (vai no PairChallenge). Só reduzir em testes.</summary>
    public int PinKdfIterations { get; set; } = 100_000;
}
