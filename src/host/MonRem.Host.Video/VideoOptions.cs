namespace MonRem.Host.Video;

/// <summary>Seção <c>MonRem:Video</c>. Desligado por padrão: esta etapa aceita qualquer aparelho da rede (DESIGN 5.9 vem depois).</summary>
public sealed class VideoOptions
{
    public const string Section = "MonRem:Video";

    public bool Enabled { get; set; }
    public int TcpPort { get; set; } = 28252;
    public int UdpPort { get; set; } = 28252;
    public int Fps { get; set; } = 30;

    /// <summary>Nome mostrado na lista do app (padrão: nome da máquina, o mesmo padrão de <c>MonRem:HostName</c>).</summary>
    public string? HostName { get; set; }
}
