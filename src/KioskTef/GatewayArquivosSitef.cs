using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KioskTef;

internal sealed class GatewayArquivosSitef : IGatewayTef
{
    private const string Pedido001 = "intpos.001";
    private const string PedidoTmp = "intpos.tmp";
    private const string Status = "intpos.sts";
    private const string Resposta001 = "intpos.001";
    private const int TentativasStatus = 28;
    private const int IntervaloMs = 500;
    private const int IntervaloMonitorMs = 3000;
    private const int ErroInterno = -999999999;
    private const string CnpjAutomacao = "26314062000161";
    private const string CnpjVazio = "00000000000000";

    private const string Crt = "CRT";
    private const string Adm = "ADM";
    private const string Cnc = "CNC";
    private const string Cnf = "CNF";
    private const string Ncn = "NCN";
    private const string Cel = "CEL";
    private const string CartDigi = "CARTDIGI";
    private const string Propriet = "PROPRIET";

    private static readonly string[] Comandos = { Crt, Adm, Cnc, Cnf, Ncn, Cel, CartDigi, Propriet };
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });
    private static readonly Lazy<string> Maquina = new(NomeMaquina);

    private readonly string _pastaRequisicao;
    private readonly string _pastaResposta;
    private readonly Func<string> _pastaTransacoes;
    private readonly Action<string> _registrar;
    private readonly Action<string, string> _evento;
    private readonly SemaphoreSlim _lockEnvio = new(1, 1);
    private readonly object _lockEstado = new();

    private CancellationTokenSource? _ctsEspera;
    private Task? _tarefaEspera;
    private CancellationTokenSource? _ctsMonitor;
    private Task? _tarefaMonitor;
    private PedidoSitef? _ultimoPedido;
    private bool _aberto;

    public GatewayArquivosSitef(string pastaRequisicao, string pastaResposta, Func<string> pastaTransacoes,
        Action<string> registrar, Action<string, string> evento)
    {
        _pastaRequisicao = pastaRequisicao;
        _pastaResposta = pastaResposta;
        _pastaTransacoes = pastaTransacoes;
        _registrar = registrar;
        _evento = evento;
    }

    public bool NovoSiTef { get; set; }
    public string Descricao => "gerenciador do SiTef";
    public Action? AoAbrir { get; set; }
    public Action? AoFechar { get; set; }
    public Action<string>? AoFalhar { get; set; }
    public Func<string, bool>? AoReceber { get; set; }

    public bool Conectado => Directory.Exists(_pastaRequisicao) && Directory.Exists(_pastaResposta);

    private sealed record PedidoSitef(int Operacao, string Comando, bool Pix, string Identificacao, List<KeyValuePair<string, string>> Campos);

    private sealed class Leitura
    {
        public long Tamanho = -1;
        public DateTime Data;
    }

    public void Iniciar()
    {
        if (_tarefaMonitor is not null)
            return;

        _ctsMonitor = new CancellationTokenSource();
        var ct = _ctsMonitor.Token;

        _tarefaMonitor = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    AtualizarEstado();
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(IntervaloMonitorMs, ct);
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
        var cts = _ctsMonitor;
        var tarefa = _tarefaMonitor;
        _ctsMonitor = null;
        _tarefaMonitor = null;

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
        await CancelarEsperaAsync();
        Fechar();
    }

    public Task ConectarAsync()
    {
        if (!Directory.Exists(_pastaRequisicao))
            throw new DirectoryNotFoundException("a pasta de pedidos " + _pastaRequisicao + " não existe");

        if (!Directory.Exists(_pastaResposta))
            throw new DirectoryNotFoundException("a pasta de respostas " + _pastaResposta + " não existe");

        Abrir();
        return Task.CompletedTask;
    }

    private void AtualizarEstado()
    {
        if (Conectado)
        {
            Abrir();
            return;
        }

        bool livre;

        lock (_lockEstado)
        {
            livre = _tarefaEspera is null || _tarefaEspera.IsCompleted;
        }

        if (livre && _lockEnvio.CurrentCount > 0)
            Fechar();
    }

    private void Abrir()
    {
        lock (_lockEstado)
        {
            if (_aberto)
                return;

            _aberto = true;
        }

        try { AoAbrir?.Invoke(); } catch { }
    }

    private void Fechar()
    {
        lock (_lockEstado)
        {
            if (!_aberto)
                return;

            _aberto = false;
        }

        try { AoFechar?.Invoke(); } catch { }
    }

    private async Task CancelarEsperaAsync()
    {
        CancellationTokenSource? cts;
        Task? tarefa;

        lock (_lockEstado)
        {
            cts = _ctsEspera;
            tarefa = _tarefaEspera;
            _ctsEspera = null;
            _tarefaEspera = null;
        }

        if (cts is null)
            return;

        cts.Cancel();

        if (tarefa is not null)
        {
            try { await tarefa; } catch { }
        }

        cts.Dispose();
    }

    public async Task EnviarAsync(string json)
    {
        await _lockEnvio.WaitAsync();

        try
        {
            await CancelarEsperaAsync();

            var req = TextoDelphi.InterpretarObjeto(json)
                      ?? throw new ExcecaoTef(SituacaoTef.NaoEnviada, "O pedido TEF não é um JSON válido; nada foi enviado ao SiTef.");

            var imediata = RespostaImediata(req);

            if (imediata is not null)
            {
                Entregar(imediata.ToJsonString(TextoDelphi.OpcoesJson));
                return;
            }

            PedidoSitef pedido;

            try
            {
                pedido = MontarPedido(req);
            }
            catch (ExcecaoTef)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Pedido TEF inválido (" + e.Message + "); nada foi enviado ao SiTef.");
            }

            PreservarOrfas(pedido);
            GravarPedido(pedido);

            lock (_lockEstado)
            {
                _ultimoPedido = pedido;
            }

            var status = await EsperarStatusAsync(pedido);

            if (status.Final is not null)
            {
                var comandoStatus = status.Final.TryGetValue("000-000", out var c) ? c : "";

                if (status.Resposta || comandoStatus is Cnf or Ncn)
                {
                    Concluir(pedido, status.Final, status.Resposta ? new[] { Resposta001, Status } : new[] { Status });
                    return;
                }
            }
            else if (RetirarPedido())
            {
                throw new ExcecaoTef(SituacaoTef.NaoEnviada, "O gerenciador do SiTef não recebeu o pedido em " + TentativasStatus * IntervaloMs / 1000 +
                                                             " segundos (o pedido foi retirado de " + _pastaRequisicao + "). " +
                                                             "Verifique se o SiTef está aberto nesta máquina.");
            }
            else
            {
                _registrar("O gerenciador do SiTef retirou o pedido " + pedido.Identificacao + " mas não confirmou em " + TentativasStatus * IntervaloMs / 1000 +
                           " segundos; aguardando a resposta.");
            }

            var aguardarStatus = status.Final is null && pedido.Comando is Cnf or Ncn;
            var cts = new CancellationTokenSource();
            var tarefa = Task.Run(() => EsperarRespostaAsync(pedido, aguardarStatus, cts.Token));

            lock (_lockEstado)
            {
                _ctsEspera = cts;
                _tarefaEspera = tarefa;
            }
        }
        finally
        {
            _lockEnvio.Release();
        }
    }

    private JsonObject? RespostaImediata(JsonObject req)
    {
        var operacao = Operacao(req);

        if (operacao == -127001)
        {
            var resposta = new JsonObject
            {
                ["retorno"] = "0",
                ["codigoErro"] = "0"
            };

            if (Maquina.Value != "")
                resposta["hostName"] = Maquina.Value;

            return resposta;
        }

        if (operacao == -127002)
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "A certificação do SiTef não é feita pelo modo arquivos do KioskMax; use o Web Connection.");

        return null;
    }

    private static int Operacao(JsonObject req)
    {
        var texto = TextoDelphi.ValorTexto(req, "operacao");

        if (texto == "")
            return 0;

        if (!decimal.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var valor))
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Operação TEF inválida: " + texto + "; nada foi enviado ao SiTef.");

        return (int)decimal.Truncate(valor);
    }

    private PedidoSitef MontarPedido(JsonObject req)
    {
        var dados = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (nome, item) in req)
        {
            if (item is JsonValue)
                dados[nome] = TextoDelphi.ValorBruto(item);
        }

        string? Campo(string nome) => dados.TryGetValue(nome, out var v) ? v : null;

        var gateway = Campo("gateway");

        if (gateway is not null && !string.Equals(gateway.Trim(), "SITEF", StringComparison.OrdinalIgnoreCase))
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "O modo arquivos do KioskMax atende apenas o gateway SITEF (recebido " + gateway + "); nada foi enviado.");

        var operacao = Operacao(req);
        var f = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["001-000"] = Campo("identificacao") ?? "1"
        };

        decimal? valor = null;
        var escala = 0;

        if (Campo("valorTransacao") is { } valorTexto)
            valor = ValorEmReais(valorTexto, out escala);

        if (Campo("nsuCTF") is { } nsu)
            f["012-000"] = nsu.Length > 9 ? nsu : nsu.PadLeft(9, '0');

        if (Campo("numeroParcelas") is { } parcelas)
            f["018-000"] = int.Parse(parcelas.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        DateTime? data = null;

        if (Campo("dataTransacao") is { } dataTexto)
            data = DateTime.ParseExact(dataTexto, "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        if (Campo("redeAdquirente") is { } rede)
            f["010-000"] = rede;

        if (Campo("codControle") is { } controle)
            f["027-000"] = controle;

        f["565-008"] = "1=" + (Campo("cnpjcpfEstabelecimento") ?? CnpjVazio) + ";2=" + CnpjAutomacao;

        var comando = Crt;
        var pix = false;

        switch (operacao)
        {
            case -99:
                comando = Adm;

                if (!NovoSiTef)
                    f["501-001"] = "00000010000001000000000000111100000000000000000000000000000000000000000000000000";
                break;
            case 6:
                comando = Cnf;
                break;
            case 191:
                comando = Ncn;
                break;
            case 300:
                comando = Propriet;
                f["001-000"] = "1";
                break;
            case 112:
                f["501-001"] = "00001000000000000000000000000000000000000000000000000000000000000000000000000000";
                break;
            case 114:
                f["501-001"] = "00001000000000000000000000000000000000000000000000000000000000000000000000000000";
                f["017-000"] = "1";
                break;
            case 113:
                f["501-001"] = "00001000000000000000000000000000000000000000000000000000000000000000000000000000";
                f["017-000"] = "0";
                break;
            case 101:
            case 106:
                f["501-001"] = "00000000001000000000000000000000000000000000000000000000000000000000000000000000";
                break;
            case 104:
            case 105:
                f["501-001"] = "00000000000000100000000000000000000000000000000000000000000000000000000000000000";
                break;
            case 103:
                f["501-001"] = "00000000000010000000000000000000000000000000000000000000000000000000000000000000";
                break;
            case 422:
                comando = CartDigi;
                f["002-000"] = "1";
                f["004-000"] = "0";
                f["504-000"] = "60110024";
                f["701-000"] = "0";
                f["701-006"] = "6";
                f["501-001"] = "00000000000000000000000000000000000000000000000000000000000000000100000000000000";
                break;
            case 128:
                var origem = Campo("operacaoOrigem") is { } o ? int.Parse(o.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : -1;
                pix = origem == 422;
                comando = pix ? CartDigi : Cnc;

                switch (origem)
                {
                    case 112:
                    case 113:
                    case 114:
                        f["501-001"] = "00000010000000000000000000000000000000000000000000000000000000000000000000000000";
                        break;
                    case 101:
                    case 103:
                    case 104:
                    case 105:
                        f["501-001"] = "00000000000001000000000000000000000000000000000000000000000000000000000000000000";
                        break;
                    case 422:
                        f["002-000"] = "1";
                        f["701-000"] = "1";
                        break;
                    default:
                        f["501-001"] = "00000010000001000000000000100000000000000000000000000000000000000000000000000000";
                        break;
                }
                break;
        }

        var campos = new List<KeyValuePair<string, string>>();

        void Por(string chave, string conteudo)
        {
            var i = campos.FindIndex(k => k.Key == chave);

            if (i >= 0)
                campos[i] = new KeyValuePair<string, string>(chave, conteudo);
            else
                campos.Add(new KeyValuePair<string, string>(chave, conteudo));
        }

        void Copiar(string chave)
        {
            if (f.TryGetValue(chave, out var conteudo))
                Por(chave, conteudo);
        }

        var crt = comando == Crt;
        var cnc = comando == Cnc;
        var cnf = comando == Cnf;
        var ncn = comando == Ncn;
        var cartDigi = comando == CartDigi;

        Por("000-000", comando);
        Copiar("001-000");

        if (crt || cnc || cnf || ncn || cartDigi)
            Copiar("002-000");

        if (cartDigi)
            Copiar("701-000");

        if ((crt || cnc || cartDigi) && valor is not null)
            Por("003-000", ValorComVirgula(valor.Value));

        if (cnf && valor is not null)
            Por("003-000", ValorJava(valor.Value, escala));

        if (cartDigi)
        {
            Copiar("504-000");
            Copiar("150-000");
        }

        if (crt || cartDigi)
        {
            Por("004-000", "0");
            Copiar("004-000");
        }

        if (cnc || cnf || ncn)
        {
            Copiar("010-000");
            Copiar("012-000");
            Copiar("010-001");
        }

        if (crt)
        {
            Copiar("017-000");
            Copiar("018-000");
        }

        if (cnc && data is not null)
        {
            Por("022-000", data.Value.ToString("ddMMyyyy", CultureInfo.InvariantCulture));
            Por("023-000", data.Value.ToString("hhmmss", CultureInfo.InvariantCulture));
        }

        if (cnf || ncn)
            Copiar("027-000");

        if (crt)
            Copiar("050-000");

        Copiar("501-001");
        Copiar("565-008");

        if (cnf)
            Copiar("701-006");

        if (comando == Propriet)
        {
            Por("400-006", "2");
            Por("565-009", "011111CPF CONFIRME O CPF");
        }

        Por("999-999", "0");

        return new PedidoSitef(operacao, comando, pix, f["001-000"], campos);
    }

    private static decimal ValorEmReais(string texto, out int escala)
    {
        escala = 0;

        if (texto.Trim().Length == 0)
            return 0m;

        var valor = decimal.Parse(texto, NumberStyles.Float, CultureInfo.InvariantCulture);
        escala = (decimal.GetBits(valor)[3] >> 16) & 0xFF;
        return valor / 100m;
    }

    private static string ValorComVirgula(decimal valor)
    {
        var truncado = decimal.Truncate(valor * 100m) / 100m;
        return truncado.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
    }

    private static string ValorJava(decimal valor, int escala)
    {
        var formato = escala > 0 ? "0." + new string('0', escala) + "############################" : "0.############################";
        return valor.ToString(formato, CultureInfo.InvariantCulture);
    }

    private void GravarPedido(PedidoSitef pedido)
    {
        var temporario = Path.Combine(_pastaRequisicao, PedidoTmp);
        var definitivo = Path.Combine(_pastaRequisicao, Pedido001);
        var sb = new StringBuilder();

        foreach (var (chave, conteudo) in pedido.Campos)
            sb.Append(chave).Append(" = ").Append(conteudo).Append("\r\n");

        try
        {
            File.WriteAllBytes(temporario, Ansi.Value.GetBytes(sb.ToString()));
            File.Move(temporario, definitivo, false);
        }
        catch (Exception e)
        {
            try { File.Delete(temporario); } catch { }
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Não foi possível gravar o pedido em " + _pastaRequisicao + ": " + e.Message);
        }

        _registrar("PEDIDO SITEF (" + Pedido001 + "): " + string.Join(" | ", pedido.Campos.Select(c => c.Key + " = " + c.Value)));
    }

    private bool RetirarPedido()
    {
        var definitivo = Path.Combine(_pastaRequisicao, Pedido001);

        try
        {
            if (!File.Exists(definitivo))
                return false;

            File.Delete(definitivo);
            return !File.Exists(definitivo);
        }
        catch
        {
            return false;
        }
    }

    private void PreservarOrfas(PedidoSitef novo)
    {
        string[] arquivos;

        try
        {
            arquivos = Directory.Exists(_pastaResposta) ? Directory.GetFiles(_pastaResposta) : Array.Empty<string>();
        }
        catch (Exception e)
        {
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Não foi possível verificar a pasta " + _pastaResposta + ": " + e.Message);
        }

        var pedidoAntigo = Path.Combine(_pastaRequisicao, Pedido001);
        var temPedidoAntigo = File.Exists(pedidoAntigo);

        if (arquivos.Length == 0 && !temPedidoAntigo)
            return;

        var destino = Path.Combine(_pastaTransacoes(), "resp-orfas");
        var carimbo = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);

        try
        {
            Directory.CreateDirectory(destino);
        }
        catch (Exception e)
        {
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Não foi possível criar " + destino + " para preservar respostas antigas: " + e.Message);
        }

        var lidas = new List<(string Nome, Dictionary<string, string>? Mapa, string Preservado)>();

        foreach (var arquivo in arquivos)
        {
            var nome = Path.GetFileName(arquivo);
            Dictionary<string, string>? mapa = null;

            if (string.Equals(nome, Resposta001, StringComparison.OrdinalIgnoreCase) || string.Equals(nome, Status, StringComparison.OrdinalIgnoreCase))
            {
                try { mapa = LerMapa(arquivo); } catch { }
            }

            var preservado = Mover(arquivo, destino, carimbo + "_" + nome);
            lidas.Add((nome, mapa, preservado));
        }

        if (temPedidoAntigo)
        {
            var preservado = Mover(pedidoAntigo, destino, carimbo + "_req_" + Pedido001);
            _registrar("Pedido antigo não retirado pelo SiTef movido de " + _pastaRequisicao + " para " + preservado + ".");
        }

        PedidoSitef? anterior;

        lock (_lockEstado)
        {
            anterior = _ultimoPedido;
        }

        foreach (var (nome, mapa, preservado) in lidas)
        {
            if (mapa is null)
            {
                _registrar("Arquivo " + nome + " encontrado em " + _pastaResposta + " antes do pedido " + novo.Identificacao + "; preservado em " + preservado + ".");
                continue;
            }

            var ident = mapa.TryGetValue("001-000", out var i) ? i.Trim() : "";
            var comando = mapa.TryGetValue("000-000", out var c) ? c.Trim() : "";
            var detalhe = "ident " + (ident == "" ? "?" : ident) +
                          " comando " + (comando == "" ? "?" : comando) +
                          " nsu " + (mapa.TryGetValue("012-000", out var n) ? n.Trim() : "") +
                          " status " + (mapa.TryGetValue("009-000", out var s) ? s.Trim() : "") +
                          " (" + nome + " preservado em " + preservado + ")";

            _registrar("TEF_RESPOSTA_ORFA: " + detalhe + " " + string.Join(" | ", mapa.Select(k => k.Key + " = " + k.Value)));
            _evento("TEF_RESPOSTA_ORFA", detalhe);

            var final = string.Equals(nome, Resposta001, StringComparison.OrdinalIgnoreCase) || comando is Cnf or Ncn;

            if (!final || ident == "" || GerenciadorSitef.MesmaIdentificacao(ident, novo.Identificacao))
                continue;

            var contexto = anterior is not null && GerenciadorSitef.MesmaIdentificacao(ident, anterior.Identificacao)
                ? anterior
                : new PedidoSitef(0, comando, false, ident, new List<KeyValuePair<string, string>>());

            var convertida = Converter(contexto, mapa);

            if (TextoDelphi.ValorTexto(TextoDelphi.InterpretarObjeto(convertida), "identificacao") != "")
                Entregar(convertida);
        }
    }

    private static string Mover(string origem, string pasta, string nome)
    {
        var destino = Path.Combine(pasta, nome);

        for (var n = 1; File.Exists(destino); n++)
            destino = Path.Combine(pasta, nome + "." + n);

        try
        {
            File.Move(origem, destino);
        }
        catch (Exception e)
        {
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Não foi possível preservar " + origem + " antes do novo pedido (" + e.Message + "); nada foi enviado ao SiTef.");
        }

        return destino;
    }

    private async Task<(Dictionary<string, string>? Final, bool Resposta)> EsperarStatusAsync(PedidoSitef pedido)
    {
        var leituraStatus = new Leitura();
        var leituraResposta = new Leitura();

        for (var tentativa = 0; tentativa < TentativasStatus; tentativa++)
        {
            var resposta = LerSeCompleto(Path.Combine(_pastaResposta, Resposta001), pedido.Identificacao, leituraResposta);

            if (resposta is not null)
                return (resposta, true);

            var status = LerSeCompleto(Path.Combine(_pastaResposta, Status), pedido.Identificacao, leituraStatus);

            if (status is not null)
            {
                _registrar("STATUS SITEF: " + string.Join(" | ", status.Select(k => k.Key + " = " + k.Value)));
                return (status, false);
            }

            await Task.Delay(IntervaloMs);
        }

        return (null, false);
    }

    private async Task EsperarRespostaAsync(PedidoSitef pedido, bool aguardarStatus, CancellationToken ct)
    {
        var leitura = new Leitura();
        var leituraStatus = new Leitura();
        var arquivo = Path.Combine(_pastaResposta, aguardarStatus ? Status : Resposta001);

        while (!ct.IsCancellationRequested)
        {
            var mapa = LerSeCompleto(arquivo, pedido.Identificacao, leitura);

            if (mapa is null && aguardarStatus)
            {
                mapa = LerSeCompleto(Path.Combine(_pastaResposta, Resposta001), pedido.Identificacao, leituraStatus);

                if (mapa is not null)
                    aguardarStatus = false;
            }

            if (mapa is not null)
            {
                Concluir(pedido, mapa, aguardarStatus ? new[] { Status } : new[] { Resposta001, Status });
                return;
            }

            try
            {
                await Task.Delay(IntervaloMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Concluir(PedidoSitef pedido, Dictionary<string, string> mapa, string[] arquivos)
    {
        _registrar("RESPOSTA SITEF: " + string.Join(" | ", mapa.Select(k => k.Key + " = " + k.Value)));

        if (Entregar(Converter(pedido, mapa)))
        {
            foreach (var nome in arquivos)
            {
                var caminho = Path.Combine(_pastaResposta, nome);

                try
                {
                    if (nome == Status && File.Exists(caminho))
                    {
                        var status = LerMapa(caminho);

                        if (status.TryGetValue("001-000", out var ident) && !GerenciadorSitef.MesmaIdentificacao(ident, pedido.Identificacao))
                            continue;
                    }

                    File.Delete(caminho);
                }
                catch (Exception e)
                {
                    _registrar("Não foi possível apagar " + caminho + " após registrar a resposta: " + e.Message);
                }
            }
        }
        else
        {
            _registrar("A resposta do pedido " + pedido.Identificacao + " não foi registrada; os arquivos ficaram em " + _pastaResposta + ".");
        }
    }

    private bool Entregar(string json)
    {
        try
        {
            return AoReceber?.Invoke(json) ?? false;
        }
        catch (Exception e)
        {
            _registrar("Erro ao registrar a resposta do SiTef: " + e.Message);
            return false;
        }
    }

    private Dictionary<string, string>? LerSeCompleto(string arquivo, string identificacao, Leitura leitura)
    {
        try
        {
            var info = new FileInfo(arquivo);

            if (!info.Exists)
            {
                leitura.Tamanho = -1;
                return null;
            }

            var mapa = LerMapa(arquivo);

            if (!mapa.TryGetValue("001-000", out var ident) || !GerenciadorSitef.MesmaIdentificacao(ident, identificacao))
                return null;

            if (mapa.ContainsKey("999-999"))
                return mapa;

            var estavel = leitura.Tamanho == info.Length && leitura.Data == info.LastWriteTimeUtc;
            leitura.Tamanho = info.Length;
            leitura.Data = info.LastWriteTimeUtc;
            return estavel ? mapa : null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> LerMapa(string arquivo)
    {
        string texto;

        using (var fs = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var leitor = new StreamReader(fs, Ansi.Value, false))
            texto = leitor.ReadToEnd();

        var mapa = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var linha in texto.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (linha.Length < 10)
                continue;

            mapa[linha.Substring(0, 7)] = linha.Substring(10);
        }

        return mapa;
    }

    private string Converter(PedidoSitef pedido, Dictionary<string, string> mapa)
    {
        try
        {
            return MontarResposta(pedido, mapa).ToJsonString(TextoDelphi.OpcoesJson);
        }
        catch (Exception e)
        {
            _registrar("Não foi possível converter a resposta do SiTef: " + e.Message);
            return new JsonObject { ["erro"] = e.Message, ["identificacao"] = pedido.Identificacao }.ToJsonString(TextoDelphi.OpcoesJson);
        }
    }

    private static JsonObject MontarResposta(PedidoSitef pedido, Dictionary<string, string> mapa)
    {
        var tef = InterpretarResposta(mapa);

        if (TransacaoOk(tef))
        {
            switch (pedido.Operacao)
            {
                case -99:
                case 6:
                case 300:
                    tef["009-000"] = 0;
                    break;
                case 128:
                    tef["statusTransacao"] = pedido.Pix ? "X" : "Z";
                    break;
                case 191:
                    tef["009-000"] = 0;
                    tef["statusTransacao"] = "D";
                    break;
                case 422:
                    tef["statusTransacao"] = "C";
                    break;
                default:
                    tef["statusTransacao"] = "P";
                    break;
            }
        }

        return ConverterResposta(tef, pedido.Operacao);
    }

    private static Dictionary<string, object?> InterpretarResposta(Dictionary<string, string> mapa)
    {
        var tef = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (chave, bruto) in mapa)
        {
            var valor = bruto.Trim();

            if (valor.Length == 0)
                continue;

            if (chave is "028-000" or "028-001" || chave.Contains("029-"))
                continue;

            switch (chave)
            {
                case "001-000":
                case "004-000":
                case "018-000":
                    tef[chave] = decimal.Parse(valor, NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
                case "003-000":
                    tef[chave] = decimal.Parse(valor.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
                case "022-000":
                    tef[chave] = DateTime.ParseExact(valor, "ddMMyyyy", CultureInfo.InvariantCulture);
                    break;
                case "023-000":
                    if (mapa.TryGetValue("022-000", out var dia))
                        tef[chave] = DateTime.ParseExact(dia + valor, "ddMMyyyyHHmmss", CultureInfo.InvariantCulture);
                    else
                    {
                        var hora = DateTime.ParseExact(valor, "HHmmss", CultureInfo.InvariantCulture);
                        tef[chave] = new DateTime(1970, 1, 1, hora.Hour == 12 ? 0 : hora.Hour, hora.Minute, hora.Second);
                    }
                    break;
                default:
                    tef[chave] = valor;
                    break;
            }
        }

        MontarCupom(tef, mapa);
        return tef;
    }

    private static void MontarCupom(Dictionary<string, object?> tef, Dictionary<string, string> mapa)
    {
        var quebras = new HashSet<int>();

        if (mapa.TryGetValue("028-001", out var marcas))
        {
            foreach (var parte in marcas.Split(':'))
                quebras.Add(int.Parse(parte.Trim(), CultureInfo.InvariantCulture));
        }

        if (!mapa.TryGetValue("028-000", out var quantidadeTexto))
            return;

        var quantidade = int.Parse(quantidadeTexto.Trim(), CultureInfo.InvariantCulture);

        if (quantidade <= 0)
            return;

        var comprovantes = new List<string>();
        var sb = new StringBuilder();

        for (var i = 1; i <= quantidade; i++)
        {
            if (sb.Length > 0)
                sb.Append('\n');

            var linha = mapa.TryGetValue("029-" + i.ToString("000", CultureInfo.InvariantCulture), out var l) ? l : "";
            sb.Append(linha.Replace("\"", ""));

            if (quebras.Contains(i))
            {
                comprovantes.Add(sb.ToString());
                sb.Clear();
            }
        }

        comprovantes.Add(sb.ToString());
        tef["029-"] = comprovantes.Count == 0 ? "" : comprovantes[0];
        tef["029-COMPROVANTES"] = comprovantes;
    }

    private static bool TransacaoOk(Dictionary<string, object?> tef)
    {
        if (tef.Count == 0)
            return false;

        var comando = tef.TryGetValue("000-000", out var c) ? c as string : null;

        if (comando is null || Array.IndexOf(Comandos, comando) < 0)
            throw new InvalidOperationException("Comando não identificado.");

        if (comando is Cnf or Ncn)
            return tef.GetValueOrDefault("000-000") is not null && tef.GetValueOrDefault("001-000") is not null && tef.GetValueOrDefault("999-999") is not null;

        return Texto(tef.GetValueOrDefault("009-000")) == "0";
    }

    private static string Texto(object? valor)
    {
        return valor switch
        {
            null => "null",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            int n => n.ToString(CultureInfo.InvariantCulture),
            _ => valor.ToString() ?? ""
        };
    }

    private static JsonObject ConverterResposta(Dictionary<string, object?> tef, int operacao)
    {
        object? Valor(string chave) => tef.TryGetValue(chave, out var v) ? v : null;

        var retorno = ErroInterno;

        if (int.TryParse(Texto(Valor("009-000")), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r))
            retorno = r;

        var autorizacao = Valor("013-000") ?? Valor("602-000") ?? Valor("521-001");
        var saida = new List<KeyValuePair<string, object?>>();

        void Por(string chave, object? valor)
        {
            var i = saida.FindIndex(k => k.Key == chave);

            if (i >= 0)
                saida[i] = new KeyValuePair<string, object?>(chave, valor);
            else
                saida.Add(new KeyValuePair<string, object?>(chave, valor));
        }

        Por("operacao", operacao);
        Por("numeroTransacao", Valor("012-000"));
        Por("retorno", retorno);
        Por("codigoErro", retorno);
        Por("codigoRespAutorizadora", autorizacao);
        Por("codigoAprovacao", autorizacao);
        Por("valorTransacao", Valor("003-000"));
        Por("numeroParcelas", Valor("018-000"));
        Por("nsuCTF", Valor("012-000"));
        Por("nsuAutorizadora", Valor("043-000"));
        Por("bandeira", Valor("010-003"));
        Por("redeAdquirente", Valor("010-000"));
        Por("codRedeAdquirente", Valor("010-001"));
        Por("cartao", Valor("010-004"));
        Por("mensagem", Valor("030-000"));
        Por("estabelecimento", Valor("150-000"));
        Por("codControle", Valor("027-000"));
        Por("statusTransacao", Valor("statusTransacao"));
        Por("dataPreDatado", Valor("019-001"));
        Por("identificacao", Valor("001-000"));

        var principal = Valor("029-");

        if (Valor("029-COMPROVANTES") is not List<string> { Count: > 1 } comprovantes)
        {
            Por("cupomEstabelecimento", principal);
            Por("cupomCliente", principal);
            Por("cupomReduzido", principal);
        }
        else
        {
            Por("cupomEstabelecimento", comprovantes[0]);
            Por("cupomCliente", comprovantes[1]);
            Por("cupomReduzido", comprovantes.Count > 2 ? comprovantes[2] : principal);
        }

        if (Valor("023-000") is not null)
            Por("dataTransacao", Valor("023-000"));
        else if (Valor("022-000") is not null)
            Por("dataTransacao", Valor("022-000"));

        if (operacao == 300)
        {
            if (Valor("565-010") is not { } cpf)
            {
                Por("codigoErro", 0m);
                Por("retorno", 0m);
                Por("retornoCpfPinPad", null);
            }
            else
                Por("retornoCpfPinPad", Texto(cpf).Substring(4));
        }

        if (Maquina.Value != "")
            Por("hostName", Maquina.Value);

        var json = new JsonObject();

        foreach (var (chave, valor) in saida)
        {
            switch (valor)
            {
                case null:
                    break;
                case decimal d when chave == "valorTransacao":
                    json[chave] = (d * 100m).ToString(CultureInfo.InvariantCulture).Split('.')[0];
                    break;
                case DateTime dt:
                    json[chave] = dt.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
                    break;
                case string s when chave == "mensagem":
                    json["display"] = new JsonArray(new JsonObject { ["mensagem"] = s });
                    break;
                case string s when chave is "cupomReduzido" or "cupomEstabelecimento" or "cupomCliente":
                    var linhas = new JsonArray();

                    foreach (var linha in DividirComoJava(s))
                        linhas.Add(new JsonObject { ["linha"] = linha });

                    json[chave] = linhas;
                    break;
                default:
                    json[chave] = Texto(valor);
                    break;
            }
        }

        return json;
    }

    private static IEnumerable<string> DividirComoJava(string texto)
    {
        if (!texto.Contains('\n'))
            return new[] { texto };

        var partes = texto.Split('\n').ToList();

        while (partes.Count > 0 && partes[^1].Length == 0)
            partes.RemoveAt(partes.Count - 1);

        return partes;
    }

    private static string NomeMaquina()
    {
        try
        {
            var nome = Dns.GetHostName();
            var ip = Dns.GetHostAddresses(nome).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
            return ip + ":" + nome;
        }
        catch
        {
            return "";
        }
    }
}
