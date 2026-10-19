namespace KioskTef;

internal interface IGatewayTef
{
    string Descricao { get; }
    bool Conectado { get; }
    Action? AoAbrir { get; set; }
    Action? AoFechar { get; set; }
    Action<string>? AoFalhar { get; set; }
    Func<string, bool>? AoReceber { get; set; }
    void Iniciar();
    Task PararAsync();
    Task ConectarAsync();
    Task DesconectarAsync();
    Task EnviarAsync(string json);
}
