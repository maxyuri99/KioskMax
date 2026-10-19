namespace KioskTef;

public sealed class EstadoTef
{
    public bool Servidor { get; init; }
    public bool WebConnection { get; init; }
    public bool Ocupado { get; init; }
    public bool Pendente { get; init; }
    public int Porta { get; init; }
    public string Erro { get; init; } = "";
    public string? UltimaIdentificacao { get; init; }
    public string? UltimaSituacao { get; init; }
    public DateTimeOffset? UltimaEm { get; init; }
}
