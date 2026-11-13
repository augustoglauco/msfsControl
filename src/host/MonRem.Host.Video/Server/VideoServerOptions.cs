namespace MonRem.Host.Video.Server;

public sealed class VideoServerOptions
{
    public string HostName { get; set; } = Environment.MachineName;
    public int TcpPort { get; set; } = 28252;
    public int UdpPort { get; set; } = 28252;
    public int Fps { get; set; } = 30;

    /// <summary>Quadros que podem estar sem ACK (SPEC 12.3 e 14: janela 2).</summary>
    public int AckWindow { get; set; } = 2;

    /// <summary>Tempo para o cliente se identificar depois de conectar.</summary>
    public TimeSpan IdentifyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Sem nenhuma mensagem do cliente por esse tempo, a sessão termina (SPEC 16.6c: padrão do serviço 45 s).</summary>
    public TimeSpan ConnectionLossTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Acima disso em qualquer eixo, a identificação cai para 1280x720.</summary>
    public int MaxDimension { get; set; } = 8192;
}
