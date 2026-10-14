using System.Net;

namespace NpsShared.Utilities;

public static class PaginasLocais
{
    public const string Aguardando = "AGUARDANDO";
    public const string SemTela = "SEM_TELA";
    public const string Bloqueado = "BLOQUEADO";

    public static bool EhTipoValido(string? tipo) =>
        tipo == Aguardando || tipo == SemTela || tipo == Bloqueado;

    public static string Mensagem(string tipo) => tipo switch
    {
        Aguardando => "Aprove este kiosk no Gestão",
        SemTela => "Sem tela definida para este kiosk",
        Bloqueado => "Este kiosk está bloqueado",
        _ => ""
    };

    public static string Titulo(string tipo) => tipo switch
    {
        Aguardando => "Aguardando aprovação",
        SemTela => "Sem tela definida",
        Bloqueado => "Kiosk bloqueado",
        _ => ""
    };

    public static string Renderizar(string tipo, string nome, string identificador, string? mensagem = null)
    {
        var titulo = WebUtility.HtmlEncode(Titulo(tipo));
        var texto = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(mensagem) ? Mensagem(tipo) : mensagem);
        var nomeHtml = WebUtility.HtmlEncode(nome ?? "");
        var idHtml = WebUtility.HtmlEncode(identificador ?? "");

        return $$"""
<!DOCTYPE html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<title>UP+ Kiosk</title>
<style>
html,body{margin:0;height:100%;background:#243d92;color:#fff;font-family:'Segoe UI',Arial,sans-serif;overflow:hidden;cursor:none}
.centro{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;padding:0 5vw}
h1{font-size:12vh;margin:0;letter-spacing:.05em;font-weight:700}
h2{font-size:5vh;margin:3vh 0 1vh;font-weight:600}
p{font-size:3.4vh;margin:0;opacity:.9}
.dados{margin-top:6vh;font-size:2.6vh;opacity:.75;line-height:1.6}
.dados b{font-weight:600}
.relogio{position:fixed;right:3vw;bottom:3vh;font-size:3vh;opacity:.8;font-variant-numeric:tabular-nums}
</style>
</head>
<body>
<div class="centro">
<h1>UP+</h1>
<h2>{{titulo}}</h2>
<p>{{texto}}</p>
<div class="dados"><div><b>Kiosk:</b> {{nomeHtml}}</div><div><b>Identificador:</b> {{idHtml}}</div></div>
</div>
<div class="relogio" id="relogio"></div>
<script>
function atualizar(){var d=new Date();document.getElementById('relogio').textContent=d.toLocaleDateString('pt-BR')+' '+d.toLocaleTimeString('pt-BR');}
atualizar();setInterval(atualizar,1000);
</script>
</body>
</html>
""";
    }
}
