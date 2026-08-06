using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OptiscalerClient.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OptiscalerClient.Views
{
    public partial class DebugWindow : Window
    {
        private const int MaxLogLines = 2000;
        private readonly StringBuilder _logContent = new StringBuilder();
        private readonly Queue<int> _lineLengths = new Queue<int>();
        private static DebugWindow? _instance;
        public static DebugWindow? Instance => _instance;
        public static bool IsLoggingEnabled => _instance != null;

        public DebugWindow()
        {
            InitializeComponent();
            _instance = this;

            this.Closed += (s, e) => _instance = null;

            Log("Debug Window Initialized");
        }

        public DebugWindow(bool isStartup) : this()
        {
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public static void Log(string message)
        {
            if (_instance == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                var txtLogs = _instance.FindControl<SelectableTextBlock>("TxtLogs");
                var scroll = _instance.FindControl<ScrollViewer>("LogScrollViewer");

                if (txtLogs != null)
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    string line = $"[{timestamp}] {message}{Environment.NewLine}";
                    _instance._logContent.Append(line);
                    _instance._lineLengths.Enqueue(line.Length);

                    while (_instance._lineLengths.Count > MaxLogLines)
                    {
                        var trimLength = _instance._lineLengths.Dequeue();
                        if (_instance._logContent.Length >= trimLength)
                        {
                            _instance._logContent.Remove(0, trimLength);
                        }
                        else
                        {
                            _instance._logContent.Clear();
                            _instance._lineLengths.Clear();
                            break;
                        }
                    }

                    txtLogs.Text = _instance._logContent.ToString();

                    if (scroll != null) scroll.ScrollToEnd();
                }
            });
        }

        public static void Log(Func<string> messageFactory)
        {
            if (_instance == null) return;
            Log(messageFactory());
        }

        private void BtnClear_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _logContent.Clear();
            _lineLengths.Clear();
            var txtLogs = this.FindControl<SelectableTextBlock>("TxtLogs");
            if (txtLogs != null) txtLogs.Text = "";
        }

        private async void BtnCopy_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                    await topLevel.Clipboard.SetTextAsync(_logContent.ToString());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DebugWindow] Clipboard copy failed: {ex.Message}"); }
        }

        private void BtnOpenCacheFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                PlatformServiceFactory.CreateShellService().OpenFolder(OptiscalerClient.Services.AppPaths.GetAppDataRoot());
            }
            catch (Exception ex)
            {
                Log(() => $"[DebugWindow] Failed to open cache folder: {ex.Message}");
            }
        }
    }
}
