// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using System;
using System.Threading.Tasks;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    /// <summary>
    /// Short step-by-step walkthrough shown once on startup (see MainWindow.ShowQuickTourIfNeededAsync)
    /// and reachable later from Help > Getting Started.
    /// </summary>
    public partial class QuickTourWindow : Window, IGamepadInputHost
    {
        // Icon glyph (FontIcons), title key, paragraph keys. Steps 1-2 and the titles of 1-3 reuse the Getting Started help strings.
        // Each paragraph is split on line breaks; lines starting with "• " are rendered as bullet points.
        private static readonly (string Icon, string TitleKey, string[] TextKeys)[] Steps =
        {
            ("\uF68F", "TxtHelpGSStep1Label", new[] { "TxtHelpGSStep1Text" }),
            ("\uEEB9", "TxtHelpGSStep2Label", new[] { "TxtHelpGSStep2Text" }),
            ("\uF6AA", "TxtHelpGSStep3Label", new[] { "TxtTourStep3Text" }),
            ("\uF710", "TxtTourStepExtrasLabel", new[] { "TxtTourStepExtrasText" }),
        };

        private const double SlideOffset = 28;
        private static readonly TimeSpan StepOutDuration = TimeSpan.FromMilliseconds(130);
        private static readonly TimeSpan StepInDuration = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan DotDuration = TimeSpan.FromMilliseconds(260);

        private readonly bool _animationsEnabled = AnimationHelper.GetPanelAnimationDuration() > TimeSpan.Zero;
        private readonly Border[] _dots = new Border[Steps.Length];
        private int _currentStep;
        private int _transitionId;
        private GamepadDialogNavigationHelper? _gamepadHelper;

        GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

        public QuickTourWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
        }

        public QuickTourWindow(Window owner)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            WindowScreenFitHelper.FitToScreen(this);

            // Flicker-free startup: start invisible, show after positioning
            Opacity = 0;
            DialogCenterHelper.Register(this, owner);

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
                titleBar.PointerPressed += (s, e) => BeginMoveDrag(e);

            // Start from an explicit transform so the first slide animates instead of jumping from null.
            var content = this.FindControl<StackPanel>("PnlStepContent");
            if (content != null) content.RenderTransform = TranslateX(0);

            BuildDots();
            ShowStep(0);

            Opened += (s, e) =>
            {
                Opacity = 1;
                var rootPanel = this.FindControl<Panel>("RootPanel");
                if (rootPanel != null)
                {
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }

                _gamepadHelper ??= new GamepadDialogNavigationHelper(this, null);
                if (owner is IGamepadInputHost host)
                    host.GamepadHelper?.SuspendInput();

                this.FindControl<Button>("BtnNext")?.Focus();
            };

            Closed += (s, e) =>
            {
                if (owner is IGamepadInputHost closedHost)
                    closedHost.GamepadHelper?.ResumeInput();

                _gamepadHelper?.Dispose();
                _gamepadHelper = null;
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void BuildDots()
        {
            var dots = this.FindControl<StackPanel>("PnlStepDots");
            if (dots == null) return;

            for (int i = 0; i < _dots.Length; i++)
            {
                var dot = new Border { Height = 6, Width = 6, CornerRadius = new CornerRadius(3) };
                if (_animationsEnabled)
                {
                    dot.Transitions = new Transitions
                    {
                        new DoubleTransition { Property = WidthProperty, Duration = DotDuration, Easing = new CubicEaseOut() },
                        new BrushTransition { Property = Border.BackgroundProperty, Duration = DotDuration }
                    };
                }
                _dots[i] = dot;
                dots.Children.Add(dot);
            }
        }

        /// <summary>Moves to another step, sliding the current card out and the new one in from the travel direction.</summary>
        private async Task GoToStepAsync(int index)
        {
            if (index < 0 || index >= Steps.Length || index == _currentStep) return;

            var content = this.FindControl<StackPanel>("PnlStepContent");
            if (!_animationsEnabled || content == null)
            {
                ShowStep(index);
                return;
            }

            int id = ++_transitionId;
            double direction = index > _currentStep ? 1 : -1;
            _currentStep = index;

            // Out: fade and drift towards the opposite side of travel. The dots morph meanwhile.
            SetContentTransitions(content, StepOutDuration, new CubicEaseIn());
            content.Opacity = 0;
            content.RenderTransform = TranslateX(-SlideOffset * direction);
            ShowDots(index);
            await Task.Delay(StepOutDuration);
            if (id != _transitionId) return;

            // Swap content and jump (without transition) to the entry side.
            content.Transitions = null;
            content.RenderTransform = TranslateX(SlideOffset * direction);
            ShowStep(index);

            // In: on the next frame, so the jump above is not animated.
            await Task.Delay(16);
            if (id != _transitionId) return;
            SetContentTransitions(content, StepInDuration, new CubicEaseOut());
            content.Opacity = 1;
            content.RenderTransform = TranslateX(0);
        }

        private static void SetContentTransitions(Control content, TimeSpan duration, Easing easing)
        {
            content.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = easing },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = duration, Easing = easing }
            };
        }

        private static ITransform TranslateX(double x) =>
            TransformOperations.Parse(FormattableString.Invariant($"translateX({x}px)"));

        private void ShowStep(int index)
        {
            _currentStep = index;
            var step = Steps[index];
            bool isLast = index == Steps.Length - 1;

            var icon = this.FindControl<TextBlock>("TxtStepIcon");
            if (icon != null) icon.Text = step.Icon;

            var title = this.FindControl<TextBlock>("TxtStepTitle");
            if (title != null) title.Text = GetResourceString(step.TitleKey, string.Empty);

            var textPanel = this.FindControl<StackPanel>("PnlStepText");
            if (textPanel != null)
            {
                textPanel.Children.Clear();
                foreach (var key in step.TextKeys)
                {
                    foreach (var line in GetResourceString(key, string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        textPanel.Children.Add(CreateTextLine(line.Trim()));
                }
            }

            var counter = this.FindControl<TextBlock>("TxtStepCounter");
            if (counter != null) counter.Text = $"{index + 1} / {Steps.Length}";

            var btnBack = this.FindControl<Button>("BtnBack");
            if (btnBack != null) btnBack.IsVisible = index > 0;

            var btnSkip = this.FindControl<Button>("BtnSkip");
            if (btnSkip != null) btnSkip.IsVisible = !isLast;

            var btnNext = this.FindControl<Button>("BtnNext");
            if (btnNext != null)
                btnNext.Content = GetResourceString(isLast ? "TxtGotIt" : "TxtTourNext", isLast ? "Got it" : "Next");

            ShowDots(index);
        }

        /// <summary>The active dot stretches into a pill; the Width/Background transitions animate the morph.</summary>
        private void ShowDots(int index)
        {
            var active = this.FindResource("BrAccent") as IBrush;
            var inactive = this.FindResource("BrBorderSubtle") as IBrush;
            for (int i = 0; i < _dots.Length; i++)
            {
                if (_dots[i] == null) continue;
                _dots[i].Width = i == index ? 22 : 6;
                _dots[i].Background = i == index ? active : inactive;
            }
        }

        private Control CreateTextLine(string line)
        {
            bool isBullet = line.StartsWith("• ");
            var text = new TextBlock
            {
                Text = isBullet ? line.Substring(2) : line,
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                Foreground = this.FindResource("BrTextSecondary") as IBrush
            };
            if (!isBullet) return text;

            var bullet = new Border
            {
                Width = 5,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = this.FindResource("BrAccent") as IBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, 8, 10, 0)
            };
            Grid.SetColumn(text, 1);
            return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { bullet, text } };
        }

        private void BtnNext_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentStep < Steps.Length - 1)
                _ = GoToStepAsync(_currentStep + 1);
            else
                _ = CloseAnimated();
        }

        private void BtnBack_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentStep > 0)
                _ = GoToStepAsync(_currentStep - 1);
        }

        private void BtnSkip_Click(object? sender, RoutedEventArgs e) => _ = CloseAnimated();

        private bool _isAnimatingClose = false;

        private async Task CloseAnimated()
        {
            if (_isAnimatingClose) return;
            _isAnimatingClose = true;
            DialogDimHelper.HideDimNow(this);
            var rootPanel = this.FindControl<Panel>("RootPanel");
            if (rootPanel != null) rootPanel.Opacity = 0;
            await Task.Delay(220);
            Close();
        }

        private string GetResourceString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key, out var res) == true && res is string str ? str : fallback;
        }
    }
}
