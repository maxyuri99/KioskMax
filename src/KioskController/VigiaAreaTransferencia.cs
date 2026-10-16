using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KioskController;

internal sealed class VigiaAreaTransferencia : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr JanelaSoDeMensagens = new(-3);

    private readonly Action<string> _aoCopiar;
    private bool _registrado;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public VigiaAreaTransferencia(Action<string> aoCopiar)
    {
        _aoCopiar = aoCopiar;
        CreateHandle(new CreateParams { Caption = "UpMaisLeitora", Parent = JanelaSoDeMensagens });
        _registrado = AddClipboardFormatListener(Handle);

        if (!_registrado)
        {
            var erro = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(erro);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            var texto = LerTexto();
            if (!string.IsNullOrEmpty(texto))
                _aoCopiar(texto);
        }

        base.WndProc(ref m);
    }

    private static string? LerTexto()
    {
        for (var tentativa = 0; tentativa < 5; tentativa++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
            return;

        if (_registrado)
            RemoveClipboardFormatListener(Handle);

        _registrado = false;
        DestroyHandle();
    }
}
