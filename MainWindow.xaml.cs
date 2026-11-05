using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VisualBarTimer;

public partial class MainWindow : Window
{
    private const int MaxSeconds = 120 * 60;
    private readonly Stopwatch elapsed = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(255, 100, 104));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(244, 184, 74));
    private static readonly Brush Mint = new SolidColorBrush(Color.FromRgb(84, 214, 160));
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(99, 181, 246));
    private static readonly Brush Violet = new SolidColorBrush(Color.FromRgb(177, 138, 245));
    private static readonly Brush[] SegmentColors =
    [
        Red, Red, Red, Red, Red,
        Amber, Amber, Amber, Amber, Amber,
        Mint, Mint, Mint, Mint, Mint,
        Blue, Blue, Blue, Blue, Blue,
        Violet, Violet, Violet, Violet, Violet
    ];
    private static readonly Brush Unlit = new SolidColorBrush(Color.FromRgb(40, 49, 53));
    private static readonly Brush Digits = new SolidColorBrush(Color.FromRgb(204, 233, 249));
    private static readonly string[] DigitSegments =
        ["abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg"];
    private static readonly Geometry[] SegmentShapes =
    [
        Geometry.Parse("M4,0 H14 L16,2 L14,4 H4 L2,2 Z"),
        Geometry.Parse("M16,3 L18,5 V13 L16,15 L14,13 V5 Z"),
        Geometry.Parse("M16,17 L18,19 V27 L16,29 L14,27 V19 Z"),
        Geometry.Parse("M4,28 H14 L16,30 L14,32 H4 L2,30 Z"),
        Geometry.Parse("M2,17 L4,19 V27 L2,29 L0,27 V19 Z"),
        Geometry.Parse("M2,3 L4,5 V13 L2,15 L0,13 V5 Z"),
        Geometry.Parse("M4,14 H14 L16,16 L14,18 H4 L2,16 Z")
    ];

    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VisualBarTimer", "duration-minutes.txt");
    private readonly string layoutPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(SettingsPath)!, "window-layout.json");
    private int durationSeconds = LoadDuration();
    private int durationAtDragStart;
    private int wheelRemainder;
    private Point resizeOrigin;
    private Size resizeStartSize;
    private int lastDisplayed = -1;
    private int lastLit = -1;
    private double barStepOrigin;
    private bool alarmRinging;
    private readonly SoundPlayer alarm = new(
        typeof(MainWindow).Assembly.GetManifestResourceStream("VisualBarTimer.TimerAlarm.wav"));

    private readonly SoundPlayer dragClick = new(
        typeof(MainWindow).Assembly.GetManifestResourceStream("VisualBarTimer.DragClick.wav"));
    private readonly SoundPlayer buttonBeep = new(
        typeof(MainWindow).Assembly.GetManifestResourceStream("VisualBarTimer.ButtonBeep.wav"));
    private readonly Stopwatch sinceDragClick = new();

    public MainWindow()
    {
        InitializeComponent();
        Segments.Columns = SegmentColors.Length;
        for (int i = 0; i < Segments.Columns; i++)
            Segments.Children.Add(new Border { Margin = new Thickness(.6, 0, .6, 0), CornerRadius = new CornerRadius(1) });

        ticker.Tick += (_, _) => RefreshDisplay();
        dragClick.Load();
        buttonBeep.Load();
        Loaded += (_, _) => RestoreWindowLayout();
        Closing += (_, _) =>
        {
            var origin = PointToScreen(new Point());
            var layout = new WindowLayout((int)origin.X, (int)origin.Y, Width, Height);
            SaveSetting(layoutPath, System.Text.Json.JsonSerializer.Serialize(layout));
        };
        Closed += (_, _) =>
        {
            ticker.Stop();
            StopAlarm();
            alarm.Dispose();
            dragClick.Dispose();
            buttonBeep.Dispose();
        };
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        double seconds = Math.Max(0, durationSeconds - elapsed.Elapsed.TotalSeconds);
        int displaySeconds = (int)Math.Ceiling(seconds);
        if (displaySeconds != lastDisplayed)
        {
            lastDisplayed = displaySeconds;
            DrawTime(displaySeconds);
        }

        int lit = (int)Math.Ceiling(seconds / durationSeconds * Segments.Columns);
        if (lit != lastLit)
        {
            lastLit = lit;
            for (int i = 0; i < Segments.Children.Count; i++)
                ((Border)Segments.Children[i]).Background = i < lit ? SegmentColors[i] : Unlit;
        }

        if (seconds == 0 && elapsed.IsRunning)
        {
            elapsed.Stop();
            ticker.Stop();
            SetAdjustmentEnabled(false);
            alarmRinging = true;
            StartButton.Content = "STOP";
            alarm.PlayLooping();
        }
    }

    private void DrawTime(int seconds)
    {
        string time = $"{seconds / 60:00}:{seconds % 60:00}";
        AutomationProperties.SetName(TimeReadout, time);
        TimeDisplay.Children.Clear();
        double left = TimeDisplay.Width - (time.Length - 1) * 19 - 5;
        foreach (char character in time)
        {
            if (character == ':')
            {
                AddDigitShape(new EllipseGeometry(new Point(2, 10), 1.5, 1.5), left);
                AddDigitShape(new EllipseGeometry(new Point(2, 22), 1.5, 1.5), left);
                left += 5;
                continue;
            }

            foreach (char segment in DigitSegments[character - '0'])
                AddDigitShape(SegmentShapes[segment - 'a'], left);
            left += 19;
        }
    }

    private void AddDigitShape(Geometry geometry, double left)
    {
        var shape = new Path { Data = geometry, Fill = Digits };
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, 1);
        TimeDisplay.Children.Add(shape);
    }

    private void ToggleTimer(object sender, RoutedEventArgs e)
    {
        ToggleCountdown();
        if (!alarmRinging) buttonBeep.Play();
    }

    private void ToggleCountdown()
    {
        if (alarmRinging)
        {
            ResetCountdown();
            return;
        }

        if (elapsed.IsRunning)
        {
            RefreshDisplay();
            if (alarmRinging)
            {
                ResetCountdown();
                return;
            }
            elapsed.Stop();
            ticker.Stop();
            SetAdjustmentEnabled(true);
            StartButton.Content = "START";
        }
        else
        {
            elapsed.Start();
            ticker.Start();
            SetAdjustmentEnabled(false);
            StartButton.Content = "STOP";
        }
        RefreshDisplay();
    }

    private void ResetTimer(object sender, RoutedEventArgs e)
    {
        ResetCountdown();
        buttonBeep.Play();
    }

    private void ResetCountdown()
    {
        StopAlarm();
        elapsed.Reset();
        ticker.Stop();
        SetAdjustmentEnabled(true);
        StartButton.Content = "START";
        RefreshDisplay();
    }

    private void StopAlarm()
    {
        if (!alarmRinging) return;
        alarm.Stop();
        alarmRinging = false;
    }

    private void SetAdjustmentEnabled(bool enabled)
    {
        if (!enabled) wheelRemainder = 0;
        BarAdjust.IsEnabled = enabled;
        BarAdjust.IsHitTestVisible = enabled;
    }

    private void BarStarted(object sender, DragStartedEventArgs e)
    {
        durationAtDragStart = durationSeconds;
        barStepOrigin = Mouse.GetPosition(this).X;
    }

    private void BarMoved(object sender, DragDeltaEventArgs e)
    {
        const double PixelsPerStep = 8;
        int steps = (int)((Mouse.GetPosition(this).X - barStepOrigin) / PixelsPerStep);
        if (steps == 0) return;
        barStepOrigin += steps * PixelsPerStep;
        AdjustDuration(steps);
    }

    private void AdjustDuration(int steps)
    {
        int adjusted = Math.Clamp(durationSeconds + steps * 60, 60, MaxSeconds);
        if (adjusted == durationSeconds) return;

        // Coalesce large pointer jumps and limit the sound during fast drags.
        if (!sinceDragClick.IsRunning || sinceDragClick.ElapsedMilliseconds >= 45)
        {
            dragClick.Play();
            sinceDragClick.Restart();
        }

        durationSeconds = adjusted;
        ResetCountdown();
    }

    private static int LoadDuration()
    {
        try
        {
            if (System.IO.File.Exists(SettingsPath) &&
                int.TryParse(System.IO.File.ReadAllText(SettingsPath), out int minutes) &&
                minutes >= 1 && minutes <= MaxSeconds / 60)
                return minutes * 60;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
        }

        return 30 * 60;
    }

    private void BarCompleted(object sender, DragCompletedEventArgs e)
    {
        if (durationSeconds != durationAtDragStart) SaveDuration();
    }

    private void BarWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!BarAdjust.IsEnabled || BarAdjust.IsDragging) return;

        const int WheelDeltaPerStep = 120;
        wheelRemainder += e.Delta;
        int steps = wheelRemainder / WheelDeltaPerStep;
        wheelRemainder %= WheelDeltaPerStep;
        int previousDuration = durationSeconds;
        AdjustDuration(steps);
        if (durationSeconds != previousDuration) SaveDuration();
    }

    private void SaveDuration()
    {

        SaveSetting(SettingsPath, (durationSeconds / 60).ToString());
    }

    private static void SaveSetting(string path, string contents)
    {
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(tempPath, contents);
            System.IO.File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
            try { System.IO.File.Delete(tempPath); }
            catch (Exception cleanupError) when (cleanupError is System.IO.IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(cleanupError);
            }
        }
    }

    private void ResizeStarted(object sender, DragStartedEventArgs e)
    {
        resizeOrigin = PointToScreen(Mouse.GetPosition(this));
        resizeStartSize = new Size(ActualWidth, ActualHeight);
    }

    private void ResizeMoved(object sender, DragDeltaEventArgs e)
    {
        Vector movement = PointToScreen(Mouse.GetPosition(this)) - resizeOrigin;
        var dpi = VisualTreeHelper.GetDpi(this);
        SetTimerSize(resizeStartSize.Width + movement.X / dpi.DpiScaleX,
            resizeStartSize.Height + movement.Y / dpi.DpiScaleY);
    }

    private void ResizePreset(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        double fraction = double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture);
        double width = MinWidth + fraction * (MaxWidth - MinWidth);
        double height = MinHeight + fraction * (MaxHeight - MinHeight);
        var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        FitWindowToMonitor(monitor, PointToScreen(new Point()), width, height);
    }

    private sealed record WindowLayout(int X, int Y, double Width, double Height);

    private void RestoreWindowLayout()
    {
        try
        {
            if (!System.IO.File.Exists(layoutPath)) return;

            var layout = System.Text.Json.JsonSerializer.Deserialize<WindowLayout>(
                System.IO.File.ReadAllText(layoutPath));
            if (layout is null || !double.IsFinite(layout.Width) || !double.IsFinite(layout.Height))
                return;

            var origin = new Point(layout.X, layout.Y);
            var monitor = MonitorFromPoint(new NativePoint(layout.X, layout.Y), 2);
            FitWindowToMonitor(monitor, origin, layout.Width, layout.Height);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            Debug.WriteLine(error);
        }
    }

    private void FitWindowToMonitor(IntPtr monitor, Point requestedOrigin, double width, double height)
    {
        width = Math.Clamp(width, MinWidth, MaxWidth);
        height = Math.Clamp(height, MinHeight, MaxHeight);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var origin = PointToScreen(new Point());
            double workWidth = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
            double workHeight = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
            width = Math.Max(MinWidth, Math.Min(width, workWidth));
            height = Math.Max(MinHeight, Math.Min(height, workHeight));
            double x = Math.Clamp(requestedOrigin.X, info.Work.Left,
                Math.Max(info.Work.Left, info.Work.Right - width * dpi.DpiScaleX));
            double y = Math.Clamp(requestedOrigin.Y, info.Work.Top,
                Math.Max(info.Work.Top, info.Work.Bottom - height * dpi.DpiScaleY));
            Left += (x - origin.X) / dpi.DpiScaleX;
            Top += (y - origin.Y) / dpi.DpiScaleY;
        }
        SetTimerSize(width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void SetTimerSize(double width, double height)
    {
        Width = Math.Clamp(width, MinWidth, MaxWidth);
        Height = Math.Clamp(height, MinHeight, MaxHeight);
        ReadoutColumn.Width = new GridLength(117 + (Height - MinHeight) * 1.5);
    }

    private void ShowHelp(object sender, RoutedEventArgs e)
    {
        foreach (Window owned in OwnedWindows)
        {
            if (owned is HelpWindow)
            {
                owned.Activate();
                return;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(TimerPanel);
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(TimerPanel.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(TimerPanel.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        picture.Render(TimerPanel);
        picture.Freeze();

        new HelpWindow(picture) { Owner = this }.Show();
    }

    private void DragHeader(object sender, MouseButtonEventArgs e) => DragMove();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}








