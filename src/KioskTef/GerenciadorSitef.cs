using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KioskTef;

public sealed class GerenciadorSitef : IDisposable
{
    private const int DiasArquivos = 60;
    private static readonly UTF8Encoding Utf8Bom = new(true);

    private readonly SemaphoreSlim _lockTransacao = new(1, 1);
    private readonly SemaphoreSlim _lockConexao = new(1, 1);
    private readonly SemaphoreSlim _sinal = new(0, int.MaxValue);
    private readonly object _lockDados = new();
    private readonly object _lockArquivo = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _tarefaReconexao;
    private bool _aguardando;
    private bool _conectado;
    private string _identAtual = "";
    private string _operAtual = "";
    private string _arquivoAtual = "";
    private string _respostaFinal = "";
    private string _respostaParcial = "";
    private string _erroSocket = "";
    private bool _parcialNova;
    private volatile bool _parando;

    public string Pasta { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TEFManagerUP");
    public string Endereco { get; set; } = "ws://localhost:9098/tef";
    public int IntervaloReconexaoMs { get; set; } = 3000;
    public Action<string>? AoRegistrar { get; set; }
    public Action<bool>? AoMudarConexao { get; set; }

    public bool Conectado => _socket is { State: WebSocketState.Open };

    public bool Ocupado
    {
        get
        {
            lock (_lockDados)
            {
                return _aguardando;
            }
        }
    }

    private string PastaTransacoes => Path.Combine(Pasta, "transacoes");
    private string PastaLog => Path.Combine(Pasta, "log");

    public void ConfigurarPasta(string pasta)
    {
        Pasta = pasta;
        LimparArquivosAntigos();
    }

    public void Iniciar()
    {
        if (_tarefaReconexao is not null)
            return;

        _parando = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _tarefaReconexao = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!Conectado)
                        await ConectarAsync();
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(IntervaloReconexaoMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    public async Task PararAsync()
    {
        _parando = true;
        var cts = _cts;
        var tarefa = _tarefaReconexao;
        _cts = null;
        _tarefaReconexao = null;

        if (cts is not null)
        {
            cts.Cancel();

            if (tarefa is not null)
            {
                try { await tarefa; } catch { }
            }

            cts.Dispose();
        }

        await DesconectarAsync();
    }

    public async Task DesconectarAsync()
    {
        var ws = _socket;

        if (ws is null)
            return;

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(2000);
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
        }
        catch
        {
        }

        try { ws.Abort(); } catch { }
    }

    public async Task ConectarAsync()
    {
        await _lockConexao.WaitAsync();

        try
        {
            if (Conectado)
                return;

            var ws = new ClientWebSocket();

            try
            {
                using var cts = new CancellationTokenSource(5000);
                await ws.ConnectAsync(new Uri(Endereco), cts.Token);
            }
            catch
            {
                ws.Dispose();
                throw;
            }

            _socket = ws;
            SocketOpen();
            _ = Task.Run(() => LoopReceberAsync(ws));
        }
        finally
        {
            _lockConexao.Release();
        }
    }

    private async Task LoopReceberAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16384];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;

                do
                {
                    r = await ws.ReceiveAsync(buffer, CancellationToken.None);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        return;
                    }

                    ms.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);

                var texto = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);

                try
                {
                    SocketMessage(texto);
                }
                catch (Exception e)
                {
                    Log("Erro ao processar mensagem do Web Connection: " + e.Message);
                }
            }
        }
        catch (Exception e)
        {
            if (!_parando)
                SocketError(e.Message);
        }
        finally
        {
            SocketClose(ws);
            try { ws.Dispose(); } catch { }
        }
    }

    private void Log(string mensagem)
    {
        var linha = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture) + " - " + mensagem;

        lock (_lockArquivo)
        {
            try
            {
                Directory.CreateDirectory(PastaLog);
                var arquivo = Path.Combine(PastaLog, "TEF_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                File.AppendAllText(arquivo, linha + Environment.NewLine, Utf8Bom);
            }
            catch
            {
            }
        }

        Notificar(mensagem.Length > 300 ? mensagem.Substring(0, 300) : mensagem);
    }

    private void Notificar(string mensagem)
    {
        try { AoRegistrar?.Invoke(mensagem); } catch { }
    }

    private static string Limpar(string valor)
    {
        var sb = new StringBuilder();

        foreach (var c in valor)
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                sb.Append(c);

        return sb.Length == 0 ? "0" : sb.ToString();
    }

    private string NomeArquivo(string operacao, string identificacao)
    {
        return Path.Combine(PastaTransacoes, Limpar(operacao) + "_" + Limpar(identificacao) + ".json");
    }

    private JsonObject? LerArquivo(string arquivo)
    {
        lock (_lockArquivo)
        {
            if (arquivo == "" || !File.Exists(arquivo))
                return null;

            try
            {
                return TextoDelphi.InterpretarObjeto(File.ReadAllText(arquivo, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }
    }

    private void GravarArquivo(string arquivo, JsonObject obj)
    {
        lock (_lockArquivo)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
                File.WriteAllText(arquivo, obj.ToJsonString(TextoDelphi.OpcoesJson), Utf8Bom);
            }
            catch (Exception e)
            {
                Notificar("Não foi possível gravar " + arquivo + ": " + e.Message);
            }
        }
    }

    private string LocalizarArquivo(string identificacao, string operacao)
    {
        if (TextoDelphi.Aparar(identificacao) == "")
            return "";

        if (TextoDelphi.Aparar(operacao) != "")
        {
            var exato = NomeArquivo(operacao, identificacao);

            if (File.Exists(exato))
                return exato;
        }

        if (!Directory.Exists(PastaTransacoes))
            return "";

        var padrao = "*" + Path.GetFileName(NomeArquivo("", identificacao)).Substring(1);
        var resultado = "";
        var maisNovo = DateTime.MinValue;

        try
        {
            foreach (var arq in Directory.GetFiles(PastaTransacoes, padrao))
            {
                var data = File.GetLastWriteTime(arq);

                if (data > maisNovo)
                {
                    maisNovo = data;
                    resultado = arq;
                }
            }
        }
        catch
        {
        }

        return resultado;
    }

    private void RegistrarMensagem(string arquivo, string mensagem, JsonObject? json, bool final)
    {
        var obj = LerArquivo(arquivo) ?? new JsonObject();

        if (obj["mensagens"] is not JsonArray mensagens)
        {
            obj.Remove("mensagens");
            mensagens = new JsonArray();
            obj.Add("mensagens", mensagens);
        }

        mensagens.Add(new JsonObject
        {
            ["em"] = TextoDelphi.Agora(),
            ["texto"] = mensagem
        });

        obj.Remove("atualizadoEm");
        obj.Add("atualizadoEm", TextoDelphi.Agora());

        if (final && json is not null)
        {
            obj.Remove("resposta");
            obj.Add("resposta", json.DeepClone());
            obj.Remove("situacao");
            obj.Add("situacao", SituacaoTef.Concluida);
        }

        GravarArquivo(arquivo, obj);
    }

    public void LimparArquivosAntigos()
    {
        try
        {
            if (Directory.Exists(PastaTransacoes))
                foreach (var arq in Directory.GetFiles(PastaTransacoes, "*.json"))
                    if ((int)(DateTime.Now - File.GetLastWriteTime(arq)).TotalDays > DiasArquivos)
                        File.Delete(arq);

            if (Directory.Exists(PastaLog))
                foreach (var arq in Directory.GetFiles(PastaLog, "*.log"))
                    if ((int)(DateTime.Now - File.GetLastWriteTime(arq)).TotalDays > DiasArquivos)
                        File.Delete(arq);
        }
        catch
        {
        }
    }

    internal static bool MesmaIdentificacao(string a, string b)
    {
        if (TextoDelphi.TentarInt64(a, out var va) && va >= 0 && TextoDelphi.TentarInt64(b, out var vb) && vb >= 0)
            return va == vb;

        return string.Equals(TextoDelphi.Aparar(a), TextoDelphi.Aparar(b), StringComparison.OrdinalIgnoreCase);
    }

    private void SocketOpen()
    {
        bool log;

        lock (_lockDados)
        {
            log = !_conectado;
            _conectado = true;
        }

        if (log)
        {
            Log("Conectado ao Web Connection.");
            try { AoMudarConexao?.Invoke(true); } catch { }
        }
    }

    private void SocketClose(ClientWebSocket ws)
    {
        bool log;

        lock (_lockDados)
        {
            if (ReferenceEquals(_socket, ws))
                _socket = null;

            log = _conectado;
            _conectado = false;
        }

        if (log)
        {
            Log("Conexão com o Web Connection encerrada.");
            try { AoMudarConexao?.Invoke(false); } catch { }
        }

        lock (_lockDados)
        {
            if (_aguardando && _respostaFinal == "")
            {
                if (_erroSocket == "")
                    _erroSocket = "conexão encerrada pelo Web Connection";

                _sinal.Release();
            }
        }
    }

    private void SocketError(string mensagem)
    {
        Log("Erro na comunicação com o Web Connection: " + mensagem);

        lock (_lockDados)
        {
            if (_aguardando)
            {
                _erroSocket = mensagem;
                _sinal.Release();
            }
        }
    }

    private void SocketMessage(string mensagem)
    {
        Log("RECEBIDO: " + mensagem);

        var json = TextoDelphi.InterpretarObjeto(mensagem);
        var ident = TextoDelphi.ValorTexto(json, "identificacao");
        var final = json is not null && (TextoDelphi.Contem(json, "retorno") || TextoDelphi.Contem(json, "erro"));

        lock (_lockDados)
        {
            var daAtual = _arquivoAtual != "" && (ident == "" || MesmaIdentificacao(ident, _identAtual));
            string arquivo;

            if (daAtual)
                arquivo = _arquivoAtual;
            else
            {
                arquivo = LocalizarArquivo(ident, "");

                if (arquivo == "")
                    arquivo = Path.Combine(PastaTransacoes, "avulsa_" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".json");

                Log("Resposta da identificação " + ident + " não é da transação atual (" + _identAtual + "). Registrada em " + Path.GetFileName(arquivo) + ".");
            }

            RegistrarMensagem(arquivo, mensagem, json, final);

            if (daAtual && _aguardando)
            {
                if (final)
                    _respostaFinal = mensagem;
                else
                {
                    _respostaParcial = mensagem;
                    _parcialNova = true;
                }

                _sinal.Release();
            }
        }
    }

    private void DrenarSinal()
    {
        while (_sinal.Wait(0))
        {
        }
    }

    private void Finalizar(string arquivo, string situacao)
    {
        lock (_lockDados)
        {
            var obj = LerArquivo(arquivo);

            if (obj is not null && TextoDelphi.ValorTexto(obj, "situacao") != SituacaoTef.Concluida)
            {
                obj.Remove("situacao");
                obj.Add("situacao", situacao);
                GravarArquivo(arquivo, obj);
            }
        }
    }

    public async Task<string> ExecutarAsync(string json, int timeoutMs = 30000)
    {
        if (!_lockTransacao.Wait(0))
        {
            Log("Pedido recusado: já existe uma transação em andamento. " + json);
            throw new ExcecaoTef(SituacaoTef.Ocupado, "Já existe uma transação TEF em andamento neste computador. " +
                                                      "Conclua ou cancele a transação no pinpad antes de iniciar outra.");
        }

        try
        {
            var req = TextoDelphi.InterpretarObjeto(json);
            var ident = TextoDelphi.ValorTexto(req, "identificacao");
            var oper = TextoDelphi.ValorTexto(req, "operacao");

            var registro = new JsonObject
            {
                ["identificacao"] = ident,
                ["operacao"] = oper,
                ["valorTransacao"] = TextoDelphi.ValorTexto(req, "valorTransacao"),
                ["enviadoEm"] = TextoDelphi.Agora(),
                ["situacao"] = SituacaoTef.EmAndamento
            };

            if (req is not null)
                registro["requisicao"] = req.DeepClone();
            else
                registro["requisicao"] = json;

            var arquivo = NomeArquivo(oper, ident);
            GravarArquivo(arquivo, registro);

            Log("ENVIO: " + json);

            lock (_lockDados)
            {
                _identAtual = ident;
                _operAtual = oper;
                _arquivoAtual = arquivo;
                _respostaFinal = "";
                _respostaParcial = "";
                _erroSocket = "";
                _parcialNova = false;
                _aguardando = true;
                DrenarSinal();
            }

            try
            {
                try
                {
                    await ConectarAsync();
                }
                catch (Exception e)
                {
                    if (!Conectado)
                        throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Sem comunicação com o Sankhya Web Connection: " + e.Message);
                }

                var ws = _socket;

                if (!Conectado || ws is null)
                    throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Sem comunicação com o Sankhya Web Connection. " +
                                                                 "Verifique se ele está aberto nesta máquina.");

                lock (_lockDados)
                {
                    _erroSocket = "";
                    DrenarSinal();
                }

                try
                {
                    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception e)
                {
                    throw new ExcecaoTef(SituacaoTef.Indefinida, "Falha ao enviar a transação ao Web Connection: " + e.Message);
                }

                var inicio = Stopwatch.StartNew();

                while (true)
                {
                    var espera = timeoutMs - inicio.ElapsedMilliseconds;

                    if (espera > 0)
                        await _sinal.WaitAsync((int)Math.Min(espera, int.MaxValue - 1));

                    string erro;

                    lock (_lockDados)
                    {
                        if (_respostaFinal != "")
                            return _respostaFinal;

                        erro = _erroSocket;
                        _parcialNova = false;
                        DrenarSinal();
                    }

                    if (erro != "")
                        throw new ExcecaoTef(SituacaoTef.Indefinida, "A comunicação com o Web Connection falhou durante a transação: " + erro);

                    if (inicio.ElapsedMilliseconds >= timeoutMs)
                        throw new ExcecaoTef(SituacaoTef.Indefinida, "Tempo esgotado aguardando a resposta do SiTef.");
                }
            }
            catch (ExcecaoTef e)
            {
                Finalizar(arquivo, e.Situacao == SituacaoTef.NaoEnviada ? SituacaoTef.NaoEnviada : SituacaoTef.SemResposta);
                Log("ERRO (" + e.Situacao + "): " + e.Message);
                throw;
            }
        }
        finally
        {
            lock (_lockDados)
            {
                _aguardando = false;
            }

            _lockTransacao.Release();
        }
    }

    public JsonObject Consultar(string identificacao, string operacao)
    {
        var resultado = new JsonObject();
        bool emCurso;

        lock (_lockDados)
        {
            emCurso = _aguardando && MesmaIdentificacao(identificacao, _identAtual) &&
                      (TextoDelphi.Aparar(operacao) == "" ||
                       string.Equals(TextoDelphi.Aparar(operacao), _operAtual, StringComparison.OrdinalIgnoreCase));
        }

        if (emCurso)
        {
            resultado["situacao"] = SituacaoTef.EmAndamento;
            return resultado;
        }

        var arquivo = LocalizarArquivo(identificacao, operacao);
        var obj = LerArquivo(arquivo);

        if (obj is null)
        {
            resultado["situacao"] = SituacaoTef.NaoEncontrada;
            return resultado;
        }

        var situacao = TextoDelphi.ValorTexto(obj, "situacao");

        if (situacao == SituacaoTef.EmAndamento)
            situacao = SituacaoTef.SemResposta;

        resultado["situacao"] = situacao;
        resultado["enviadoEm"] = TextoDelphi.ValorTexto(obj, "enviadoEm");

        if (obj["resposta"] is JsonObject resposta)
            resultado["resposta"] = resposta.DeepClone();

        return resultado;
    }

    public void Dispose()
    {
        try { PararAsync().GetAwaiter().GetResult(); } catch { }
    }
}
