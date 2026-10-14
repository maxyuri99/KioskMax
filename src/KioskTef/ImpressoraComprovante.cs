using System.ComponentModel;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace KioskTef;

public sealed class ImpressoraComprovante
{
    private const string FimVia = "\u0001FIMVIA";
    private const int Colunas = 42;
    private const string ComAcento = "áàâãäéèêëíìîïóòôõöúùûüçñÁÀÂÃÄÉÈÊËÍÌÎÏÓÒÔÕÖÚÙÛÜÇÑºª";
    private const string SemAcento = "aaaaaeeeeiiiiooooouuuucnAAAAAEEEEIIIIOOOOOUUUUCNoa";

    private readonly object _lockImpressao = new();
    private readonly Func<ConfiguracaoImpressao> _config;

    public ImpressoraComprovante(Func<ConfiguracaoImpressao> config)
    {
        _config = config;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDatatype;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string nome, out IntPtr handle, IntPtr padrao);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr handle, int nivel, ref DocInfo1 doc);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] dados, int tamanho, out int escrito);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    public static IReadOnlyList<string> ListarImpressoras()
    {
        var lista = new List<string>();

        foreach (string nome in PrinterSettings.InstalledPrinters)
            lista.Add(nome);

        return lista;
    }

    internal static bool LerLinhas(JsonObject comprovante, string campo, List<string> linhas)
    {
        linhas.Clear();

        if (comprovante[campo] is JsonArray vetor)
        {
            foreach (var item in vetor)
            {
                string linha;

                if (item is JsonObject o)
                    linha = o.TryGetPropertyValue("linha", out var v) ? TextoDelphi.ValorBruto(v) : "";
                else
                    linha = TextoDelphi.ValorBruto(item);

                linhas.Add(TextoDelphi.AparaDireita(linha));
            }
        }

        while (linhas.Count > 0 && TextoDelphi.Aparar(linhas[^1]) == "")
            linhas.RemoveAt(linhas.Count - 1);

        return linhas.Count > 0;
    }

    internal static bool EhViaEstabelecimento(List<string> linhas)
    {
        var texto = string.Join(Environment.NewLine, linhas);

        return texto.Contains("VIA - ESTABELECIMENTO", StringComparison.OrdinalIgnoreCase) ||
               texto.Contains("VIA ESTABELECIMENTO", StringComparison.OrdinalIgnoreCase);
    }

    internal static void MontarLinhasComprovante(JsonObject comprovante, ConfiguracaoImpressao config, List<string> linhas)
    {
        var viaCliente = new List<string>();
        var viaEstab = new List<string>();

        void AdicionarVia(List<string> via)
        {
            if (via.Count == 0)
                return;

            if (linhas.Count > 0)
                linhas.Add(FimVia);

            linhas.AddRange(via);
        }

        LerLinhas(comprovante, "cupomCliente", viaCliente);
        LerLinhas(comprovante, "cupomEstabelecimento", viaEstab);

        if (EhViaEstabelecimento(viaCliente) && !EhViaEstabelecimento(viaEstab))
        {
            if (config.ViaEstabelecimento)
                AdicionarVia(viaCliente);

            if (config.ViaCliente)
                AdicionarVia(viaEstab);
        }
        else
        {
            if (config.ViaCliente)
                AdicionarVia(viaCliente);

            if (config.ViaEstabelecimento)
                AdicionarVia(viaEstab);
        }
    }

    public void ImprimirComprovantes(JsonArray comprovantes)
    {
        var config = _config();
        var linhas = new List<string>();

        foreach (var item in comprovantes)
            if (item is JsonObject o)
                MontarLinhasComprovante(o, config, linhas);

        ImprimirLinhas(linhas);
    }

    public void ImprimirLinhas(List<string> linhas)
    {
        var config = _config();
        var impressora = config.Impressora;

        if (TextoDelphi.Aparar(impressora) == "")
            throw new Exception("Nenhuma impressora está configurada no TEF Manager. " +
                                "Abra o TEF Manager nesta máquina e escolha a impressora do comprovante.");

        if (!ListarImpressoras().Any(p => string.Equals(p, impressora, StringComparison.OrdinalIgnoreCase)))
            throw new Exception("A impressora \"" + impressora + "\" não foi encontrada neste computador. " +
                                "Verifique se ela está ligada e instalada ou escolha outra no TEF Manager.");

        if (!config.ViaCliente && !config.ViaEstabelecimento)
            throw new Exception("Nenhuma via está marcada para impressão no TEF Manager.");

        if (linhas.Count == 0)
            throw new Exception("O comprovante TEF não tem linhas para imprimir.");

        ExecutarSerializado(() =>
        {
            if (config.ModoDriver)
                ImprimirDriver(impressora, linhas);
            else
                ImprimirRaw(impressora, linhas, config.CortarPapel);
        });
    }

    public void ImprimirTeste()
    {
        var config = _config();

        var linhas = new List<string>
        {
            "         TEF MANAGER UP+",
            "       TESTE DE IMPRESSAO",
            "",
            "Impressora: " + config.Impressora,
            "Data: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            "",
            "1234567890123456789012345678901234567890"
        };

        ImprimirLinhas(linhas);
    }

    private void ExecutarSerializado(Action acao)
    {
        lock (_lockImpressao)
        {
            Exception? erro = null;

            var thread = new Thread(() =>
            {
                try
                {
                    acao();
                }
                catch (Exception e)
                {
                    erro = e;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (erro is not null)
                throw erro;
        }
    }

    private static string MensagemErro(int codigo)
    {
        return new Win32Exception(codigo).Message;
    }

    private static void ImprimirRaw(string impressora, List<string> linhas, bool cortar)
    {
        var texto = new StringBuilder();
        texto.Append("\u001b@");

        void FinalizarVia()
        {
            texto.Append("\r\n\r\n\r\n\r\n\r\n");

            if (cortar)
                texto.Append("\u001dV\u0001");
        }

        foreach (var linha in linhas)
        {
            if (linha == FimVia)
                FinalizarVia();
            else
                texto.Append(RemoverAcentos(linha)).Append("\r\n");
        }

        FinalizarVia();

        var bytes = Encoding.Latin1.GetBytes(texto.ToString());

        if (!OpenPrinter(impressora, out var handle, IntPtr.Zero))
            throw new Exception("Não foi possível acessar a impressora \"" + impressora + "\": " + MensagemErro(Marshal.GetLastWin32Error()));

        try
        {
            var doc = new DocInfo1 { pDocName = "Comprovante TEF", pOutputFile = null, pDatatype = "RAW" };

            if (StartDocPrinter(handle, 1, ref doc) == 0)
                throw new Exception("Não foi possível iniciar a impressão: " + MensagemErro(Marshal.GetLastWin32Error()));

            try
            {
                StartPagePrinter(handle);

                if (!WritePrinter(handle, bytes, bytes.Length, out _))
                    throw new Exception("Não foi possível enviar o comprovante para a impressora: " + MensagemErro(Marshal.GetLastWin32Error()));

                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    private static void ImprimirDriver(string impressora, List<string> linhas)
    {
        var doc = new PrintDocument
        {
            DocumentName = "Comprovante TEF"
        };

        doc.PrinterSettings.PrinterName = impressora;

        var indice = 0;

        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            var largura = g.VisibleClipBounds.Width;
            var altura = g.VisibleClipBounds.Height;
            var tamanho = 12f;

            var fonte = new Font("Courier New", tamanho);

            try
            {
                while (tamanho > 5 && g.MeasureString(new string('W', Colunas), fonte, int.MaxValue, StringFormat.GenericTypographic).Width > largura)
                {
                    tamanho -= 1;
                    fonte.Dispose();
                    fonte = new Font("Courier New", tamanho);
                }

                var altLinha = fonte.GetHeight(g);
                float y = 0;
                e.HasMorePages = false;

                while (indice < linhas.Count)
                {
                    var linha = linhas[indice];

                    if (linha == FimVia)
                    {
                        indice++;
                        e.HasMorePages = true;
                        return;
                    }

                    if (y + altLinha > altura && y > 0)
                    {
                        e.HasMorePages = true;
                        return;
                    }

                    g.DrawString(linha, fonte, Brushes.Black, 0, y, StringFormat.GenericTypographic);
                    y += altLinha;
                    indice++;
                }
            }
            finally
            {
                fonte.Dispose();
            }
        };

        doc.Print();
    }

    internal static string RemoverAcentos(string texto)
    {
        var chars = texto.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            var pos = ComAcento.IndexOf(chars[i]);

            if (pos >= 0)
                chars[i] = SemAcento[pos];
            else if (chars[i] > 126)
                chars[i] = ' ';
        }

        return new string(chars);
    }
}
