using System.Globalization;
using System.Text.Json;
using Serilog;

namespace NpsWatchdog;

public sealed class SobrasCameras
{
    private const string FormatoHora = "yyyy-MM-dd'T'HH':00:00'";
    private static readonly TimeSpan Validade = TimeSpan.FromDays(3);

    private readonly string _arquivo;
    private Dictionary<int, Dictionary<string, int[]>>? _sobras;

    public SobrasCameras(string arquivo)
    {
        _arquivo = arquivo;
    }

    public static SobrasCameras NaPastaDoServico() => new(Path.Combine(Baixador.PastaProtegida, "cameras-sobras.json"));

    private string Arquivo => _arquivo;

    public void Guardar(int cdCamera, IReadOnlyList<HoraContagem> horas, DateTime agora)
    {
        var sobras = Carregar();
        if (!sobras.TryGetValue(cdCamera, out var daCamera))
        {
            daCamera = new Dictionary<string, int[]>();
            sobras[cdCamera] = daCamera;
        }

        foreach (var hora in horas)
        {
            var anterior = daCamera.TryGetValue(Chave(hora.Hora), out var guardada) && guardada.Length >= 3 ? guardada : new int[3];
            daCamera[Chave(hora.Hora)] = new[] { anterior[0] + hora.QtEntradas, anterior[1] + hora.QtSaidas, anterior[2] + hora.QtReentradas };
        }

        Limpar(sobras, agora);
        Salvar(sobras);
    }

    public IReadOnlyList<HoraContagem> Descontar(int cdCamera, IReadOnlyList<HoraContagem> horas, DateTime agora)
    {
        var sobras = Carregar();
        if (Limpar(sobras, agora)) Salvar(sobras);
        if (!sobras.TryGetValue(cdCamera, out var daCamera) || daCamera.Count == 0) return horas;

        return horas.Select(hora =>
        {
            if (!daCamera.TryGetValue(Chave(hora.Hora), out var sobra) || sobra.Length < 3) return hora;
            return hora with
            {
                QtEntradas = Math.Max(0, hora.QtEntradas - sobra[0]),
                QtSaidas = Math.Max(0, hora.QtSaidas - sobra[1]),
                QtReentradas = Math.Max(0, hora.QtReentradas - sobra[2])
            };
        }).ToList();
    }

    private static string Chave(DateTime hora) => hora.ToString(FormatoHora, CultureInfo.InvariantCulture);

    private static bool Limpar(Dictionary<int, Dictionary<string, int[]>> sobras, DateTime agora)
    {
        var limite = agora - Validade;
        var mudou = false;
        foreach (var (cdCamera, daCamera) in sobras.ToList())
        {
            foreach (var chave in daCamera.Keys.ToList())
            {
                if (DateTime.TryParseExact(chave, FormatoHora, CultureInfo.InvariantCulture, DateTimeStyles.None, out var hora) && hora >= limite)
                    continue;
                daCamera.Remove(chave);
                mudou = true;
            }

            if (daCamera.Count > 0) continue;
            sobras.Remove(cdCamera);
            mudou = true;
        }

        return mudou;
    }

    private Dictionary<int, Dictionary<string, int[]>> Carregar()
    {
        if (_sobras is not null) return _sobras;
        try
        {
            _sobras = File.Exists(Arquivo)
                ? JsonSerializer.Deserialize<Dictionary<int, Dictionary<string, int[]>>>(File.ReadAllText(Arquivo)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Não foi possível ler as sobras de contagem das câmeras");
            _sobras = new();
        }

        return _sobras;
    }

    private void Salvar(Dictionary<int, Dictionary<string, int[]>> sobras)
    {
        try
        {
            var temporario = Arquivo + ".parte";
            File.WriteAllText(temporario, JsonSerializer.Serialize(sobras));
            File.Move(temporario, Arquivo, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Não foi possível gravar as sobras de contagem das câmeras");
        }
    }
}
