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

    private const string MensagemJaEnviada = "Esta cobrança já foi enviada ao SiTef e não teve resultado; " +
                                             "confira no pinpad/relatório antes de cobrar de novo.";

    private readonly SemaphoreSlim _lockTransacao = new(1, 1);
    private readonly SemaphoreSlim _lockConexao = new(1, 1);
    private readonly SemaphoreSlim _sinal = new(0, int.MaxValue);
    private readonly object _lockDados = new();
    private readonly object _lockArquivo = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _tarefaReconexao;
    private bool _aguardando;
    private bool _pendente;
    private bool _emCurso;
    private bool _reservado;
    private long _geracao;
    private System.Threading.Timer? _timerPendente;
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
    public TimeSpan LimitePendente { get; set; } = TimeSpan.FromMinutes(15);
    public Action<string>? AoRegistrar { get; set; }
    public Action<bool>? AoMudarConexao { get; set; }
    public Action<string, string>? AoEvento { get; set; }
    public Action<bool>? AoMudarOcupado { get; set; }

    public bool Conectado => _socket is { State: WebSocketState.Open };

    public bool Ocupado
    {
        get
        {
            lock (_lockDados)
            {
                return _aguardando || _pendente;
            }
        }
    }

    public bool Pendente
    {
        get
        {
            lock (_lockDados)
            {
                return _pendente;
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

    public bool TentarReservar()
    {
        if (!_lockTransacao.Wait(0))
            return false;

        lock (_lockDados)
        {
            _reservado = true;
        }

        return true;
    }

    public void LiberarReserva()
    {
        lock (_lockDados)
        {
            if (!_reservado)
                return;

            _reservado = false;
        }

        _lockTransacao.Release();
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

    private void Evento(string evento, string detalhe)
    {
        try { AoEvento?.Invoke(evento, detalhe); } catch { }
    }

    private void AvisarOcupado(bool ocupado)
    {
        try { AoMudarOcupado?.Invoke(ocupado); } catch { }
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
        return LerArquivo(arquivo, out _);
    }

    private JsonObject? LerArquivo(string arquivo, out bool ilegivel)
    {
        ilegivel = false;

        lock (_lockArquivo)
        {
            if (arquivo == "" || !File.Exists(arquivo))
                return null;

            try
            {
                var obj = TextoDelphi.InterpretarObjeto(File.ReadAllText(arquivo, Encoding.UTF8));

                if (obj is null)
                    ilegivel = true;

                return obj;
            }
            catch
            {
                ilegivel = true;
                return null;
            }
        }
    }

    private bool GravarArquivo(string arquivo, JsonObject obj)
    {
        lock (_lockArquivo)
        {
            var temporario = arquivo + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
                var conteudo = Utf8Bom.GetBytes(obj.ToJsonString(TextoDelphi.OpcoesJson));

                using (var fs = new FileStream(temporario, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(Utf8Bom.GetPreamble());
                    fs.Write(conteudo);
                    fs.Flush(true);
                }

                File.Move(temporario, arquivo, true);
                return true;
            }
            catch (Exception e)
            {
                try { File.Delete(temporario); } catch { }
                Notificar("Não foi possível gravar " + arquivo + ": " + e.Message);
                return false;
            }
        }
    }

    private bool PreservarAnterior(string arquivo)
    {
        lock (_lockArquivo)
        {
            if (!File.Exists(arquivo))
                return true;

            try
            {
                var pasta = Path.GetDirectoryName(arquivo)!;
                var nome = Path.GetFileNameWithoutExtension(arquivo);

                for (var n = 1; ; n++)
                {
                    var destino = Path.Combine(pasta, nome + ".anterior-" + n + ".json");

                    if (File.Exists(destino))
                        continue;

                    File.Move(arquivo, destino);
                    Log("Registro anterior preservado em " + Path.GetFileName(destino) + ".");
                    return true;
                }
            }
            catch (Exception e)
            {
                Notificar("Não foi possível preservar " + arquivo + ": " + e.Message);
                return false;
            }
        }
    }

    private string LocalizarArquivo(string identificacao, string operacao, bool somenteOperacao = false)
    {
        if (TextoDelphi.Aparar(identificacao) == "" || !Directory.Exists(PastaTransacoes))
            return "";

        var operLimpa = TextoDelphi.Aparar(operacao) == "" && !somenteOperacao ? "" : Limpar(operacao);
        var identLimpa = Limpar(identificacao);
        var daOperacao = "";
        var maisNovoOperacao = DateTime.MinValue;
        var deOutra = "";
        var maisNovoOutra = DateTime.MinValue;

        try
        {
            foreach (var arq in Directory.GetFiles(PastaTransacoes, "*_*.json"))
            {
                var nome = Path.GetFileNameWithoutExtension(arq);

                if (nome.Contains('.') || nome.StartsWith("avulsa_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var separador = nome.IndexOf('_');

                if (separador <= 0 || !MesmaIdentificacao(nome[(separador + 1)..], identLimpa))
                    continue;

                var mesmaOperacao = operLimpa != "" && string.Equals(nome[..separador], operLimpa, StringComparison.OrdinalIgnoreCase);

                if (!mesmaOperacao && operLimpa != "" && somenteOperacao)
                    continue;

                var data = File.GetLastWriteTime(arq);

                if (mesmaOperacao)
                {
                    if (data > maisNovoOperacao)
                    {
                        maisNovoOperacao = data;
                        daOperacao = arq;
                    }
                }
                else if (data > maisNovoOutra)
                {
                    maisNovoOutra = data;
                    deOutra = arq;
                }
            }
        }
        catch
        {
        }

        return daOperacao != "" ? daOperacao : deOutra;
    }

    private bool RegistrarMensagem(string arquivo, string mensagem, JsonObject? json, bool final, out JsonObject registro)
    {
        var obj = LerArquivo(arquivo, out var ilegivel);

        if (ilegivel)
            PreservarAnterior(arquivo);

        obj ??= new JsonObject();

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

        obj["atualizadoEm"] = TextoDelphi.Agora();

        var primeira = false;

        if (final)
        {
            if (obj.ContainsKey("resposta") || obj.ContainsKey("respostaTexto"))
            {
                if (obj["respostas"] is not JsonArray respostas)
                {
                    obj.Remove("respostas");
                    respostas = new JsonArray();
                    obj.Add("respostas", respostas);
                }

                respostas.Add(new JsonObject
                {
                    ["em"] = TextoDelphi.Agora(),
                    ["resposta"] = json is not null ? json.DeepClone() : JsonValue.Create(mensagem)
                });
            }
            else if (json is not null)
            {
                obj["resposta"] = json.DeepClone();
                obj["situacao"] = SituacaoTef.Concluida;
                primeira = true;
            }
            else
            {
                obj["respostaTexto"] = mensagem;
                obj["situacao"] = SituacaoTef.Indefinida;
                primeira = true;
            }
        }

        GravarArquivo(arquivo, obj);
        registro = obj;
        return primeira;
    }

    public void LimparArquivosAntigos()
    {
        try
        {
            if (Directory.Exists(PastaTransacoes))
            {
                foreach (var arq in Directory.GetFiles(PastaTransacoes, "*.json"))
                    if ((int)(DateTime.Now - File.GetLastWriteTime(arq)).TotalDays > DiasArquivos)
                        File.Delete(arq);

                foreach (var arq in Directory.GetFiles(PastaTransacoes, "*.tmp"))
                    if ((int)(DateTime.Now - File.GetLastWriteTime(arq)).TotalDays > 1)
                        File.Delete(arq);
            }

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

    private static bool MesmaOperacao(string a, string b)
    {
        return string.Equals(TextoDelphi.Aparar(a), TextoDelphi.Aparar(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string DescreverResposta(string ident, string oper, JsonObject? json)
    {
        var texto = "ident " + ident + " oper " + oper;

        if (json is null)
            return texto + " resposta em formato desconhecido";

        var erro = TextoDelphi.ValorTexto(json, "erro");

        if (erro != "")
            return texto + " erro " + erro;

        return texto + " retorno " + TextoDelphi.ValorTexto(json, "retorno") +
               " nsu " + TextoDelphi.ValorTexto(json, "nsuCTF") +
               " status " + TextoDelphi.ValorTexto(json, "statusTransacao");
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

        var pendenteEncerrada = "";

        lock (_lockDados)
        {
            if (_aguardando && _respostaFinal == "")
            {
                if (_erroSocket == "")
                    _erroSocket = "conexão encerrada pelo Web Connection";

                _sinal.Release();
            }
            else if (_pendente)
            {
                pendenteEncerrada = "ident " + _identAtual + " oper " + _operAtual;
                Finalizar(_arquivoAtual, SituacaoTef.SemResposta);
                EncerrarAtual(_geracao);
            }
        }

        if (pendenteEncerrada != "")
        {
            Log("A conexão caiu com a transação pendente no SiTef (" + pendenteEncerrada + "). Registrada como " + SituacaoTef.SemResposta + ".");
            Evento("TEF_PENDENTE_ENCERRADA", pendenteEncerrada + " sem resposta: conexão com o Web Connection encerrada");
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
        var final = json is not null
            ? TextoDelphi.Contem(json, "retorno") || TextoDelphi.Contem(json, "erro")
            : TextoDelphi.PareceFinal(mensagem);
        var evento = "";
        var detalhe = "";

        lock (_lockDados)
        {
            var emCurso = _arquivoAtual != "" && (_aguardando || _pendente);
            var daAtual = emCurso && (ident == "" || MesmaIdentificacao(ident, _identAtual));
            var avulsa = false;
            string arquivo;

            if (daAtual)
                arquivo = _arquivoAtual;
            else
            {
                arquivo = LocalizarArquivo(ident, "");

                if (arquivo == "")
                {
                    avulsa = true;
                    arquivo = Path.Combine(PastaTransacoes, "avulsa_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json");
                }

                Log("Mensagem " + (ident == "" ? "sem identificação" : "da identificação " + ident) +
                    " não é da transação atual (" + (emCurso ? _identAtual : "nenhuma em curso") + "). Registrada em " + Path.GetFileName(arquivo) + ".");
            }

            var primeira = RegistrarMensagem(arquivo, mensagem, json, final && !avulsa, out var registro);

            if (avulsa)
            {
                evento = "TEF_MENSAGEM_AVULSA";
                detalhe = (ident == "" ? "sem identificação" : "ident " + ident) + ": " + (mensagem.Length > 300 ? mensagem.Substring(0, 300) : mensagem);
            }
            else if (daAtual && _aguardando)
            {
                if (final)
                {
                    if (_respostaFinal == "")
                        _respostaFinal = mensagem;
                }
                else
                {
                    _respostaParcial = mensagem;
                    _parcialNova = true;
                }

                _sinal.Release();
            }
            else if (final)
            {
                var identRegistro = daAtual ? _identAtual : TextoDelphi.ValorTexto(registro, "identificacao");
                var operRegistro = daAtual ? _operAtual : TextoDelphi.ValorTexto(registro, "operacao");

                evento = "TEF_RESPOSTA_TARDIA";
                detalhe = DescreverResposta(identRegistro == "" ? ident : identRegistro, operRegistro, json) +
                          (primeira ? "" : " (adicional; a primeira resposta gravada foi mantida)");

                if (daAtual && _pendente)
                    EncerrarAtual(_geracao);
            }
        }

        if (evento != "")
        {
            Log(evento + ": " + detalhe);
            Evento(evento, detalhe);
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
                obj["situacao"] = situacao;
                GravarArquivo(arquivo, obj);
            }
        }
    }

    private void MarcarPendente(string arquivo)
    {
        var obj = LerArquivo(arquivo);

        if (obj is null || TextoDelphi.ValorTexto(obj, "situacao") != SituacaoTef.EmAndamento)
            return;

        obj["pendenteDesde"] = TextoDelphi.Agora();
        GravarArquivo(arquivo, obj);
    }

    private void EncerrarAtual(long geracao)
    {
        bool estavaOcupado;

        lock (_lockDados)
        {
            if (!_emCurso || geracao != _geracao)
                return;

            estavaOcupado = _aguardando || _pendente;
            _emCurso = false;
            _aguardando = false;
            _pendente = false;
            _identAtual = "";
            _operAtual = "";
            _arquivoAtual = "";
            _respostaFinal = "";
            _respostaParcial = "";
            _erroSocket = "";
            _parcialNova = false;
            _timerPendente?.Dispose();
            _timerPendente = null;
            _lockTransacao.Release();
        }

        if (estavaOcupado)
            AvisarOcupado(false);
    }

    private void ExpirarPendente(long geracao)
    {
        string descricao;

        lock (_lockDados)
        {
            if (!_emCurso || !_pendente || geracao != _geracao)
                return;

            descricao = "ident " + _identAtual + " oper " + _operAtual;
            Finalizar(_arquivoAtual, SituacaoTef.Indefinida);
            EncerrarAtual(geracao);
        }

        Log("A transação pendente no SiTef (" + descricao + ") não teve resposta em " + (int)LimitePendente.TotalMinutes +
            " minutos. Registrada como " + SituacaoTef.Indefinida + " e o agente foi liberado.");
        Evento("TEF_PENDENTE_EXPIRADA", descricao + " sem resposta em " + (int)LimitePendente.TotalMinutes + " minutos: " + SituacaoTef.Indefinida);
    }

    public async Task<RespostaTef> ExecutarAsync(string json, int timeoutMs = 30000)
    {
        var req = TextoDelphi.InterpretarObjeto(json);
        var ident = TextoDelphi.ValorTexto(req, "identificacao");
        var oper = TextoDelphi.ValorTexto(req, "operacao");

        if (!_lockTransacao.Wait(0))
        {
            bool mesma;

            lock (_lockDados)
            {
                mesma = (_aguardando || _pendente) && ident != "" && MesmaIdentificacao(ident, _identAtual) && MesmaOperacao(oper, _operAtual);
            }

            if (mesma)
            {
                Log("Pedido recusado: a identificação " + ident + " já está em andamento no SiTef. " + json);
                throw new ExcecaoTef(SituacaoTef.EmAndamento, "Esta cobrança já está em andamento no SiTef. " +
                                                              "Aguarde o resultado no pinpad antes de tentar de novo.");
            }

            Log("Pedido recusado: já existe uma transação em andamento. " + json);
            throw new ExcecaoTef(SituacaoTef.Ocupado, "Já existe uma transação TEF em andamento neste computador. " +
                                                      "Conclua ou cancele a transação no pinpad antes de iniciar outra.");
        }

        long geracao;

        lock (_lockDados)
        {
            _geracao++;
            geracao = _geracao;
            _emCurso = true;
        }

        var pendente = false;

        try
        {
            var arquivo = NomeArquivo(oper, ident);

            if (ident != "")
            {
                var existente = LocalizarArquivo(ident, oper, true);

                if (existente != "")
                {
                    var anterior = LerArquivo(existente, out var ilegivel);

                    if (anterior is not null || ilegivel)
                    {
                        var situacao = TextoDelphi.ValorTexto(anterior, "situacao");

                        if (!ilegivel && situacao == SituacaoTef.Concluida && anterior!["resposta"] is JsonObject resposta)
                        {
                            Log("Pedido repetido: a identificação " + ident + " já foi concluída. Devolvendo a resposta gravada em " +
                                Path.GetFileName(existente) + " sem enviar ao SiTef.");
                            return new RespostaTef(resposta.ToJsonString(TextoDelphi.OpcoesJson), true);
                        }

                        if (ilegivel || situacao != SituacaoTef.NaoEnviada)
                        {
                            Log("Pedido recusado: a identificação " + ident + " já foi enviada ao SiTef (" +
                                (ilegivel ? "registro ilegível" : situacao == "" ? "situação desconhecida" : situacao) + "). " + json);
                            throw new ExcecaoTef(SituacaoTef.JaEnviada, MensagemJaEnviada);
                        }

                        arquivo = existente;
                    }
                }
            }

            if (!PreservarAnterior(arquivo))
            {
                Log("ERRO (" + SituacaoTef.NaoEnviada + "): não foi possível preservar o registro anterior de " + Path.GetFileName(arquivo) + ".");
                throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Não foi possível preservar o registro anterior desta transação; ela não foi enviada ao SiTef.");
            }

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

            if (!GravarArquivo(arquivo, registro))
            {
                Log("ERRO (" + SituacaoTef.NaoEnviada + "): não foi possível gravar " + Path.GetFileName(arquivo) + ".");
                throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Não foi possível registrar a transação em disco; ela não foi enviada ao SiTef.");
            }

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

            AvisarOcupado(true);

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
                            return new RespostaTef(_respostaFinal, false);

                        erro = _erroSocket;
                        _parcialNova = false;
                        DrenarSinal();

                        if (erro == "" && inicio.ElapsedMilliseconds >= timeoutMs)
                        {
                            _aguardando = false;
                            _pendente = true;
                            pendente = true;
                            MarcarPendente(arquivo);
                            _timerPendente = new System.Threading.Timer(_ => ExpirarPendente(geracao), null, LimitePendente, Timeout.InfiniteTimeSpan);
                        }
                    }

                    if (erro != "")
                        throw new ExcecaoTef(SituacaoTef.Indefinida, "A comunicação com o Web Connection falhou durante a transação: " + erro);

                    if (pendente)
                    {
                        Log("A transação " + ident + " (" + oper + ") continua pendente no SiTef; o agente segue ocupado até a resposta final.");
                        throw new ExcecaoTef(SituacaoTef.Indefinida, "Tempo esgotado aguardando a resposta do SiTef. A transação continua pendente no SiTef; " +
                                                                     "consulte o resultado antes de cobrar de novo.");
                    }
                }
            }
            catch (ExcecaoTef e)
            {
                if (!pendente)
                    Finalizar(arquivo, e.Situacao == SituacaoTef.NaoEnviada ? SituacaoTef.NaoEnviada : SituacaoTef.SemResposta);

                Log("ERRO (" + e.Situacao + "): " + e.Message);
                throw;
            }
        }
        finally
        {
            if (!pendente)
                EncerrarAtual(geracao);
        }
    }

    public JsonObject Consultar(string identificacao, string operacao)
    {
        var resultado = new JsonObject();
        bool emCurso;

        lock (_lockDados)
        {
            emCurso = (_aguardando || _pendente) && MesmaIdentificacao(identificacao, _identAtual) &&
                      (TextoDelphi.Aparar(operacao) == "" || MesmaOperacao(operacao, _operAtual));
        }

        if (emCurso)
        {
            resultado["situacao"] = SituacaoTef.EmAndamento;
            return resultado;
        }

        var arquivo = LocalizarArquivo(identificacao, operacao);
        var obj = LerArquivo(arquivo, out var ilegivel);

        if (ilegivel)
        {
            resultado["situacao"] = SituacaoTef.Indefinida;
            return resultado;
        }

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
