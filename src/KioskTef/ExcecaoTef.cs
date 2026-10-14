namespace KioskTef;

public static class SituacaoTef
{
    public const string EmAndamento = "EM_ANDAMENTO";
    public const string Concluida = "CONCLUIDA";
    public const string SemResposta = "SEM_RESPOSTA";
    public const string NaoEnviada = "NAO_ENVIADA";
    public const string NaoEncontrada = "NAO_ENCONTRADA";
    public const string Indefinida = "INDEFINIDA";
    public const string Ocupado = "OCUPADO";
}

public sealed class ExcecaoTef : Exception
{
    public string Situacao { get; }

    public ExcecaoTef(string situacao, string mensagem) : base(mensagem)
    {
        Situacao = situacao;
    }
}
