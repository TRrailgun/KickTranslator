using System.Diagnostics;
using KickTranslator.Asr;
using KickTranslator.Config;
using KickTranslator.Models;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace KickTranslator.Tray;

/// <summary>Saat yanındaki simge ve menüsü. Uygulama bu menüden kapatılır.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "KickCeviri";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly WhisperEngine _whisper;
    private readonly ModelDownloader _models;
    private readonly TranslatorOptions _opts;
    private bool _wasDownloading;

    public TrayApp(IServiceProvider sp)
    {
        _whisper = sp.GetRequiredService<WhisperEngine>();
        _models = sp.GetRequiredService<ModelDownloader>();
        _opts = sp.GetRequiredService<IOptions<TranslatorOptions>>().Value;

        _statusItem = new ToolStripMenuItem("Başlatılıyor...") { Enabled = false };
        _autostartItem = new ToolStripMenuItem("Windows açılışında başlat", null, (_, _) => ToggleAutostart())
        {
            Checked = IsAutostart(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Kick Çeviri " + AppInfo.Version) { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Kurulum ve eklenti", null, (_, _) => Program.OpenUrl("http://127.0.0.1:5757/setup"));
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Günlük klasörünü aç", null, (_, _) =>
        {
            Directory.CreateDirectory(_opts.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_opts.LogsDir}\"") { UseShellExecute = true });
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "Kick Çeviri",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Program.OpenUrl("http://127.0.0.1:5757/setup");

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var dl = _models.ProgressText;
        string status;
        if (dl != null)
        {
            status = "İndiriliyor: " + dl;
            if (!_wasDownloading)
            {
                _wasDownloading = true;
                _icon.ShowBalloonTip(4000, "Kick Çeviri", "Çeviri modelleri indiriliyor. Bu ilk kurulumda bir kez yapılır.", ToolTipIcon.Info);
            }
        }
        else
        {
            if (_wasDownloading)
            {
                _wasDownloading = false;
                _icon.ShowBalloonTip(4000, "Kick Çeviri", "Hazır! Kick'te bir yayın açabilirsiniz.", ToolTipIcon.Info);
            }
            status = _whisper.AnyLoaded ? "Hazır" : "Hazırlanıyor...";
            if (_whisper.LastError != null) status = "Hata: " + _whisper.LastError;
        }

        _statusItem.Text = status.Length > 60 ? status[..60] + "…" : status;
        var tip = "Kick Çeviri · " + status;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon sınırı
    }

    private static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null;
    }

    private void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostart()) k.DeleteValue(RunValue, false);
        else k.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        _autostartItem.Checked = IsAutostart();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
