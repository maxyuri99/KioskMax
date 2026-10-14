namespace NpsShared.Models;

public class KioskContexto
{
    public int CdKiosk { get; set; }
    public string NmKiosk { get; set; } = string.Empty;
    public int CodPdv { get; set; }
    public int CodEmp { get; set; }
    public string DsPdv { get; set; } = string.Empty;
    public string NmEmpresa { get; set; } = string.Empty;
    public string Versao { get; set; } = string.Empty;
}

public class TefImpressaoConfig
{
    public string Impressora { get; set; } = string.Empty;
    public string Modo { get; set; } = "RAW";
    public bool ViaCliente { get; set; } = true;
    public bool ViaEstabelecimento { get; set; } = true;
    public bool CortarPapel { get; set; } = true;
}
