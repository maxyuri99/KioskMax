using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KioskTef;

internal static class TextoDelphi
{
    public static readonly JsonSerializerOptions OpcoesJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Aparar(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";

        int ini = 0, fim = s.Length - 1;

        while (ini <= fim && s[ini] <= ' ')
            ini++;

        while (fim >= ini && s[fim] <= ' ')
            fim--;

        return s.Substring(ini, fim - ini + 1);
    }

    public static string AparaDireita(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";

        int fim = s.Length - 1;

        while (fim >= 0 && s[fim] <= ' ')
            fim--;

        return s.Substring(0, fim + 1);
    }

    public static JsonNode? Interpretar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return null;

        try
        {
            return JsonNode.Parse(texto);
        }
        catch
        {
            return null;
        }
    }

    public static JsonObject? InterpretarObjeto(string? texto)
    {
        return Interpretar(texto) as JsonObject;
    }

    public static string ValorBruto(JsonNode? valor)
    {
        if (valor is null)
            return "";

        if (valor is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
                return s;

            return v.ToJsonString();
        }

        return "";
    }

    public static string ValorTexto(JsonObject? obj, string campo)
    {
        if (obj is null)
            return "";

        if (!obj.TryGetPropertyValue(campo, out var valor) || valor is null)
            return "";

        return Aparar(ValorBruto(valor));
    }

    public static bool Contem(JsonObject obj, string campo)
    {
        return obj.TryGetPropertyValue(campo, out _);
    }

    public static string Agora()
    {
        return DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
    }

    public static bool TentarInt64(string s, out long valor)
    {
        valor = -1;
        s = Aparar(s);

        if (s.Length == 0)
            return false;

        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out valor);
    }
}
