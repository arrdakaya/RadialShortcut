using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RadialShortcut.Core;
using RadialShortcut.Input;

namespace RadialShortcut.App;

/// <summary>Click-through, non-activating panel that shows the gesture state live (Faz 1 testing aid).</summary>
public partial class DebugOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly ObservableCollection<string> _log = [];
    private readonly GestureSettings _settings;

    public DebugOverlay(GestureSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LogList.ItemsSource = _log;
        SourceInitialized += (_, _) => MakeClickThrough();
        Loaded += (_, _) => PlaceBottomRight();
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    public void Show(GestureEvent e)
    {
        PhaseText.Text = e.Phase.ToString();

        switch (e.Kind)
        {
            case GestureEventKind.Phase:
                Add(e.Message);
                break;
            case GestureEventKind.RingOpened:
                RingText.Text = $"Halka açık · merkez ({e.Origin.X}, {e.Origin.Y})";
                break;
            case GestureEventKind.RingMoved:
                RingText.Text = Describe(e);
                break;
            case GestureEventKind.RingReleased:
                var slice = SliceOf(e);
                Add(slice < 0 ? "Merkezde bırakıldı: iptal" : $"Seçildi: dilim {slice} ({Direction(slice)})");
                RingText.Text = "—";
                break;
            case GestureEventKind.RingCancelled:
                Add("Halka iptal edildi");
                RingText.Text = "—";
                break;
        }
    }

    private string Describe(GestureEvent e)
    {
        double dx = e.Point.X - e.Origin.X, dy = e.Point.Y - e.Origin.Y;
        var slice = SliceOf(e);
        var where = slice < 0 ? "ölü bölge" : $"dilim {slice} ({Direction(slice)})";
        return $"{RingGeometry.AngleOf(dx, dy):0}° · {Math.Sqrt(dx * dx + dy * dy):0} px · {where}";
    }

    private int SliceOf(GestureEvent e) =>
        RingGeometry.SliceAt(e.Point.X - e.Origin.X, e.Point.Y - e.Origin.Y, _settings.SliceCount, _settings.DeadZonePx);

    private string Direction(int slice) => _settings.SliceCount == 8
        ? new[] { "yukarı", "sağ üst", "sağ", "sağ alt", "aşağı", "sol alt", "sol", "sol üst" }[slice]
        : $"{slice * 360 / _settings.SliceCount}°";

    private void Add(string line)
    {
        _log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (_log.Count > 10)
            _log.RemoveAt(_log.Count - 1);
    }

    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
