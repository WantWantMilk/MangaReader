// Reader.cs - Manga Reader: WPF front end, navigation, fullscreen, zoom.
// C# 5 / .NET Framework 4.8, no external dependencies.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MangaReaderApp
{
    public enum ViewMode { Continuous, SinglePage, DoublePage }
    public enum ReadDirection { Rtl, Ltr }
    public enum ZoomMode { FitPage, FitWidth, Custom }

    // =====================================================================
    //  Page bitmaps, kept in a small LRU so scrolling stays smooth without
    //  ever holding a whole volume in memory.
    // =====================================================================
    public class ImageCache
    {
        public static readonly ImageCache Shared = new ImageCache();
        private Dictionary<string, BitmapSource> _map = new Dictionary<string, BitmapSource>();
        private List<string> _order = new List<string>();
        private int _cap = 14;

        public static BitmapSource Load(string path, int decodeWidth)
        {
            try
            {
                BitmapImage b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(path, UriKind.Absolute);
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) b.DecodePixelWidth = decodeWidth;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        public BitmapSource Get(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "#" + decodeWidth.ToString(CultureInfo.InvariantCulture);
            BitmapSource b;
            if (_map.TryGetValue(key, out b)) { _order.Remove(key); _order.Add(key); return b; }
            b = Load(path, decodeWidth);
            if (b == null) return null;
            _map[key] = b;
            _order.Add(key);
            while (_order.Count > _cap)
            {
                _map.Remove(_order[0]);
                _order.RemoveAt(0);
            }
            return b;
        }

        public void Clear() { _map.Clear(); _order.Clear(); }
    }

    /// <summary>Page width = viewport width x zoom factor (continuous mode).</summary>
    public class ZoomWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double w = (values.Length > 0 && values[0] is double) ? (double)values[0] : 0.0;
            double z = (values.Length > 1 && values[1] is double) ? (double)values[1] : 1.0;
            if (w <= 0) return 0.0;
            if (z <= 0.01) z = 1.0;
            return Math.Max(60.0, w * z);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class PageToImageConverter : IValueConverter
    {
        /// <summary>Width pages are drawn at; decoding to exactly that saves a lot of RAM.</summary>
        public static int DecodeWidth = 1100;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            PageEntry pe = value as PageEntry;
            if (pe == null) return null;
            int w = DecodeWidth;
            if (pe.Width > 0 && pe.Width < w) w = pe.Width;
            return ImageCache.Shared.Get(pe.FilePath, w);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    // =====================================================================
    //  Persisted preferences
    // =====================================================================
    public static class Settings
    {
        private static Dictionary<string, string> _map;

        private static string SettingsFile
        {
            get
            {
                string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(System.IO.Path.Combine(b, "MangaReader"), "settings.ini");
            }
        }

        private static void Ensure()
        {
            if (_map != null) return;
            _map = new Dictionary<string, string>();
            try
            {
                if (File.Exists(SettingsFile))
                    foreach (string line in File.ReadAllLines(SettingsFile))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) _map[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
        }

        public static string Get(string key, string def)
        {
            Ensure();
            string v;
            return _map.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value) { Ensure(); _map[key] = value; }

        public static void Save()
        {
            Ensure();
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile));
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in _map) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(SettingsFile, lines.ToArray());
            }
            catch { }
        }
    }

    // =====================================================================
    //  Small widget factory
    // =====================================================================
    public static class UI
    {
        public static SolidColorBrush C(byte r, byte g, byte b) { return new SolidColorBrush(Color.FromRgb(r, g, b)); }

        public static Button Btn(string text, RoutedEventHandler click, string tip)
        {
            Button b = new Button();
            b.Content = text;
            b.Foreground = Brushes.White;
            b.Background = C(52, 52, 62);
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(12, 6, 12, 6);
            b.Margin = new Thickness(2, 0, 2, 0);
            b.FontSize = 13;
            b.Cursor = Cursors.Hand;
            if (tip != null) b.ToolTip = tip;
            if (click != null) b.Click += click;
            b.Template = BtnTemplate();
            return b;
        }

        private static ControlTemplate BtnTemplate()
        {
            ControlTemplate t = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "Bd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;

            Trigger over = new Trigger();
            over.Property = UIElement.IsMouseOverProperty;
            over.Value = true;
            over.Setters.Add(new Setter(Border.BackgroundProperty, C(84, 84, 100), "Bd"));
            t.Triggers.Add(over);

            Trigger down = new Trigger();
            down.Property = Button.IsPressedProperty;
            down.Value = true;
            down.Setters.Add(new Setter(Border.BackgroundProperty, C(30, 110, 190), "Bd"));
            t.Triggers.Add(down);

            Trigger dis = new Trigger();
            dis.Property = UIElement.IsEnabledProperty;
            dis.Value = false;
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            t.Triggers.Add(dis);
            return t;
        }

        public static TextBlock Label(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.Foreground = C(200, 200, 210);
            t.FontSize = 13;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(8, 0, 2, 0);
            return t;
        }

        public static TextBlock Separator()
        {
            TextBlock t = new TextBlock();
            t.Text = "|";
            t.Foreground = C(90, 90, 100);
            t.FontSize = 13;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(8, 0, 8, 0);
            return t;
        }

        public static ComboBox Combo(string[] items, double width, string tip)
        {
            ComboBox c = new ComboBox();
            c.Width = width;
            c.Margin = new Thickness(2, 0, 4, 0);
            c.FontSize = 13;
            c.VerticalAlignment = VerticalAlignment.Center;
            foreach (string s in items) c.Items.Add(s);
            c.SelectedIndex = 0;
            if (tip != null) c.ToolTip = tip;
            return c;
        }
    }

    // =====================================================================
    //  Main window
    // =====================================================================
    public class MainWindow : Window
    {
        private Book _book = new Book();
        private ViewMode _mode = ViewMode.Continuous;
        private ReadDirection _dir = ReadDirection.Rtl;
        private ZoomMode _zoomMode = ZoomMode.FitPage;
        private double _zoom = 1.0;
        private int _index;
        private bool _fullscreen;
        private WindowState _savedState;
        private WindowStyle _savedStyle;
        private ResizeMode _savedResize;
        private double _viewportWidth = 1000;
        private double _viewportHeight = 700;
        private double _layoutWidth = -1;
        private double[] _prefix = new double[0];

        private Grid _root, _pageGrid;
        private StackPanel _pageStrip;
        private ListBox _list;
        private ScrollViewer _pageScroll;
        private Border _toolbar, _hintBar, _busy, _help;
        private TextBlock _busyText, _fileText, _pageText, _emptyText, _chapterLabel, _zoomLabel;
        private ProgressBar _busyBar;
        private ComboBox _dirBox, _modeBox, _zoomBox, _chapterBox;
        private Button _fullBtn;
        private DispatcherTimer _hideTimer, _scrollTimer;
        private bool _suppressUi;
        private string[] _pendingOpen;
        private bool _chromeHidden;      // user asked for an empty screen
        private bool _autoHidden;        // fullscreen idle auto-hide
        private Border _toast;
        private TextBlock _toastText;
        private DispatcherTimer _toastTimer;

        /// <summary>Continuous mode page width multiplier (1.0 == fit width).</summary>
        public static readonly DependencyProperty ContinuousZoomProperty =
            DependencyProperty.Register("ContinuousZoom", typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double ContinuousZoom
        {
            get { return (double)GetValue(ContinuousZoomProperty); }
            set { SetValue(ContinuousZoomProperty, value); }
        }

        public MainWindow()
        {
            Title = "Manga Reader";
            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 1180;
            Height = 860;
            MinWidth = 520;
            MinHeight = 380;
            AllowDrop = true;
            UseLayoutRounding = true;

            BuildUi();
            LoadPrefs();

            PreviewKeyDown += OnKeyDown;
            PreviewMouseWheel += OnWheel;
            SizeChanged += OnSizeChanged;
            MouseMove += OnMouseMove;
            Drop += OnDrop;
            DragOver += OnDragOver;
            Closing += delegate { Settings.Save(); };
            Loaded += delegate
            {
                if (_chromeHidden) Toast("工具栏已隐藏 · 按 Tab / H 或 Esc 恢复");
                if (_pendingOpen == null) return;
                string[] p = _pendingOpen;
                _pendingOpen = null;
                OpenPaths(p);
            };

            _hideTimer = new DispatcherTimer();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(2600);
            _hideTimer.Tick += delegate { _hideTimer.Stop(); HideChrome(); };

            _scrollTimer = new DispatcherTimer();
            _scrollTimer.Interval = TimeSpan.FromMilliseconds(110);
            _scrollTimer.Tick += delegate { _scrollTimer.Stop(); SyncIndexFromScroll(); };

            _toastTimer = new DispatcherTimer();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(2400);
            _toastTimer.Tick += delegate { _toastTimer.Stop(); if (_toast != null) _toast.Visibility = Visibility.Collapsed; };
        }

        // ------------------------------------------------------------------
        //  Construction
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            _root = new Grid();
            _root.Background = UI.C(16, 16, 18);

            BuildContinuousView();
            BuildPageView();

            _emptyText = new TextBlock();
            _emptyText.Text = "把 .mobi / .azw3 漫画文件拖到这里\n或点击左上角的「打开文件」";
            _emptyText.Foreground = UI.C(120, 120, 130);
            _emptyText.FontSize = 17;
            _emptyText.TextAlignment = TextAlignment.Center;
            _emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyText.VerticalAlignment = VerticalAlignment.Center;
            _emptyText.LineHeight = 30;

            _root.Children.Add(_list);
            _root.Children.Add(_pageScroll);
            _root.Children.Add(_emptyText);
            _root.Children.Add(BuildToolbar());
            _root.Children.Add(BuildHintBar());
            _root.Children.Add(BuildHelpOverlay());
            _root.Children.Add(BuildBusyOverlay());
            _root.Children.Add(BuildToast());

            Content = _root;
        }

        private void BuildContinuousView()
        {
            _list = new ListBox();
            _list.Background = Brushes.Black;
            _list.BorderThickness = new Thickness(0);
            _list.Padding = new Thickness(0);
            _list.SelectionMode = SelectionMode.Single;
            _list.Focusable = false;
            _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(_list, true);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
            VirtualizingPanel.SetCacheLengthUnit(_list, VirtualizationCacheLengthUnit.Page);
            VirtualizingPanel.SetCacheLength(_list, new VirtualizationCacheLength(1, 2));

            // Absolutely no padding between pages, so chapters run into each
            // other without any blank strip.
            Style itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Top));
            itemStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            ControlTemplate itemTemplate = new ControlTemplate(typeof(ListBoxItem));
            FrameworkElementFactory cpf = new FrameworkElementFactory(typeof(ContentPresenter));
            cpf.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            itemTemplate.VisualTree = cpf;
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
            _list.ItemContainerStyle = itemStyle;

            DataTemplate dt = new DataTemplate();
            FrameworkElementFactory imf = new FrameworkElementFactory(typeof(Image));
            imf.SetValue(Image.StretchProperty, Stretch.Uniform);
            imf.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            imf.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            imf.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            imf.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
            // Width follows the live viewport, scaled by the zoom factor, so
            // zooming works here too (the list scrolls sideways when > 100%).
            Binding widthBinding = new Binding("ViewportWidth");
            widthBinding.RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ScrollViewer), 1);
            Binding zoomBinding = new Binding("ContinuousZoom");
            zoomBinding.RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1);
            MultiBinding zoomedWidth = new MultiBinding();
            zoomedWidth.Bindings.Add(widthBinding);
            zoomedWidth.Bindings.Add(zoomBinding);
            zoomedWidth.Converter = new ZoomWidthConverter();
            imf.SetBinding(FrameworkElement.WidthProperty, zoomedWidth);
            Binding srcBinding = new Binding();
            srcBinding.Converter = new PageToImageConverter();
            imf.SetBinding(Image.SourceProperty, srcBinding);
            dt.VisualTree = imf;
            _list.ItemTemplate = dt;

            _list.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                ListBoxItem item = ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) as ListBoxItem;
                if (item == null) return;
                int i = _list.ItemContainerGenerator.IndexFromContainer(item);
                if (i >= 0) { _index = i; UpdateLabels(); SyncChapterBox(); }
            };
            _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScroll));
        }

        private void BuildPageView()
        {
            _pageStrip = new StackPanel();
            _pageStrip.Orientation = Orientation.Horizontal;
            _pageStrip.HorizontalAlignment = HorizontalAlignment.Center;
            _pageStrip.VerticalAlignment = VerticalAlignment.Center;

            _pageGrid = new Grid();
            _pageGrid.Background = Brushes.Black;
            _pageGrid.Children.Add(_pageStrip);

            _pageScroll = new ScrollViewer();
            _pageScroll.Background = Brushes.Black;
            _pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _pageScroll.Content = _pageGrid;
            // Keep the content at least viewport sized: it centres when small
            // and scrolls when zoomed in.
            Binding mw = new Binding("ViewportWidth");
            mw.Source = _pageScroll;
            _pageGrid.SetBinding(FrameworkElement.MinWidthProperty, mw);
            Binding mh = new Binding("ViewportHeight");
            mh.Source = _pageScroll;
            _pageGrid.SetBinding(FrameworkElement.MinHeightProperty, mh);

            // Click the left or right half to turn the page, the way a manga
            // reader is expected to behave. Using the ScrollViewer itself keeps
            // its scrollbars usable.
            _pageScroll.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (_book.Pages.Count == 0) return;
                Point p = e.GetPosition(_pageScroll);
                if (_pageScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible &&
                    p.X > _pageScroll.ActualWidth - 22) return;
                if (p.X < _pageScroll.ActualWidth / 2) ClickLeft(); else ClickRight();
            };
        }

        private Border BuildToolbar()
        {
            WrapPanel wrap = new WrapPanel();
            wrap.Orientation = Orientation.Horizontal;
            wrap.VerticalAlignment = VerticalAlignment.Center;
            wrap.Margin = new Thickness(6, 5, 6, 5);

            wrap.Children.Add(BuildMenuButton());
            wrap.Children.Add(UI.Btn("打开文件", OnOpen, "打开 MOBI / AZW3 文件，可多选；多个文件会按顺序无缝接成一本书"));
            wrap.Children.Add(UI.Separator());

            _dirBox = UI.Combo(new string[] { "从右往左 (漫画)", "从左往右" }, 132, "阅读方向：日式漫画一般是从右往左");
            _dirBox.SelectionChanged += delegate
            {
                if (_suppressUi) return;
                _dir = _dirBox.SelectedIndex == 0 ? ReadDirection.Rtl : ReadDirection.Ltr;
                Settings.Set("dir", _dir.ToString());
                if (_mode != ViewMode.Continuous) RenderSpread();
            };
            wrap.Children.Add(UI.Label("方向"));
            wrap.Children.Add(_dirBox);

            _modeBox = UI.Combo(new string[] { "连续卷动", "单页", "双页" }, 96, "显示方式：连续卷动时页面首尾相接，不留任何空白");
            _modeBox.SelectionChanged += delegate { if (!_suppressUi) SetMode(_modeBox.SelectedIndex); };
            wrap.Children.Add(UI.Label("模式"));
            wrap.Children.Add(_modeBox);

            _zoomLabel = UI.Label("缩放");
            wrap.Children.Add(_zoomLabel);
            _zoomBox = UI.Combo(new string[] { "适应页面", "适应宽度", "50%", "75%", "100%", "125%", "150%", "200%", "300%" }, 104, "单页 / 双页模式下的缩放；连续卷动模式固定适应宽度");
            _zoomBox.SelectionChanged += delegate { if (!_suppressUi) SetZoom(_zoomBox.SelectedIndex); };
            wrap.Children.Add(_zoomBox);
            wrap.Children.Add(UI.Separator());

            wrap.Children.Add(UI.Btn("‹ 上一页", delegate { PrevPage(); }, "上一页 (PageUp)"));
            wrap.Children.Add(UI.Btn("下一页 ›", delegate { NextPage(); }, "下一页 (空格 / PageDown)"));

            _pageText = new TextBlock();
            _pageText.Foreground = Brushes.White;
            _pageText.FontSize = 14;
            _pageText.MinWidth = 100;
            _pageText.TextAlignment = TextAlignment.Center;
            _pageText.VerticalAlignment = VerticalAlignment.Center;
            _pageText.Margin = new Thickness(8, 0, 8, 0);
            wrap.Children.Add(_pageText);

            _chapterLabel = UI.Label("章节");
            _chapterBox = UI.Combo(new string[0], 200, "在多个文件之间跳转");
            _chapterBox.SelectionChanged += delegate
            {
                if (_suppressUi) return;
                ComboBoxItem it = _chapterBox.SelectedItem as ComboBoxItem;
                if (it == null) return;
                GoTo((int)it.Tag);
            };
            wrap.Children.Add(_chapterLabel);
            wrap.Children.Add(_chapterBox);

            _fullBtn = UI.Btn("全屏 (F11)", delegate { ToggleFullscreen(); }, "全屏 / 退出全屏 (F11)");
            _fullBtn.Margin = new Thickness(10, 0, 2, 0);
            wrap.Children.Add(_fullBtn);
            wrap.Children.Add(UI.Btn("隐藏工具栏", delegate { ToggleChrome(); }, "隐藏工具栏，进入无干扰阅读 (Tab 或 H 切换)"));

            _fileText = new TextBlock();
            _fileText.Foreground = UI.C(150, 150, 160);
            _fileText.FontSize = 12;
            _fileText.VerticalAlignment = VerticalAlignment.Center;
            _fileText.Margin = new Thickness(12, 0, 4, 0);
            _fileText.TextTrimming = TextTrimming.CharacterEllipsis;
            _fileText.MaxWidth = 320;
            wrap.Children.Add(_fileText);

            _toolbar = new Border();
            _toolbar.Background = new SolidColorBrush(Color.FromArgb(238, 28, 28, 34));
            _toolbar.BorderBrush = UI.C(60, 60, 70);
            _toolbar.BorderThickness = new Thickness(0, 0, 0, 1);
            _toolbar.VerticalAlignment = VerticalAlignment.Top;
            _toolbar.Child = wrap;
            _toolbar.MouseEnter += delegate { ShowChrome(); _hideTimer.Stop(); };
            _toolbar.MouseLeave += delegate { if (_fullscreen) { _hideTimer.Stop(); _hideTimer.Start(); } };
            return _toolbar;
        }

        private Button BuildMenuButton()
        {
            Button b = UI.Btn("☰", null, "更多");
            ContextMenu cm = new ContextMenu();

            MenuItem open = new MenuItem();
            open.Header = "打开文件…";
            open.Click += OnOpen;

            MenuItem clear = new MenuItem();
            clear.Header = "清空图片缓存…";
            clear.Click += delegate { ClearCache(); };

            MenuItem help = new MenuItem();
            help.Header = "快捷键说明 (F1)";
            help.Click += delegate { _help.Visibility = Visibility.Visible; };

            MenuItem full = new MenuItem();
            full.Header = "全屏 / 退出全屏 (F11)";
            full.Click += delegate { ToggleFullscreen(); };

            MenuItem chrome = new MenuItem();
            chrome.Header = "隐藏 / 显示工具栏 (Tab)";
            chrome.Click += delegate { ToggleChrome(); };

            MenuItem about = new MenuItem();
            about.Header = "关于 Manga Reader";
            about.Click += delegate
            {
                MessageBox.Show(this,
                    "Manga Reader 1.0" + Environment.NewLine + Environment.NewLine +
                    "轻量漫画阅读器，直接读取 MOBI / AZW / AZW3 里内嵌的图片。" + Environment.NewLine +
                    "支持从左往右和从右往左两种阅读方向、连续卷动无间隙、全屏阅读。" + Environment.NewLine + Environment.NewLine +
                    "纯 .NET Framework + WPF 实现，不需要安装任何运行库。",
                    "关于 Manga Reader", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            cm.Items.Add(open);
            cm.Items.Add(full);
            cm.Items.Add(chrome);
            cm.Items.Add(new Separator());
            cm.Items.Add(clear);
            cm.Items.Add(help);
            cm.Items.Add(new Separator());
            cm.Items.Add(about);

            b.ContextMenu = cm;
            b.Click += delegate
            {
                cm.PlacementTarget = b;
                cm.Placement = PlacementMode.Bottom;
                cm.IsOpen = true;
            };
            return b;
        }

        private void ClearCache()
        {
            if (MessageBox.Show(this,
                    "清空解压出来的图片缓存？" + Environment.NewLine +
                    "已经打开的漫画会重新载入一次。",
                    "清空图片缓存", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            string[] sources = new string[_book.Chapters.Count];
            for (int i = 0; i < _book.Chapters.Count; i++) sources[i] = _book.Chapters[i].SourcePath;

            try
            {
                string root = BookLoader.DefaultCacheRoot;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "清空缓存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _book = new Book();
            _list.ItemsSource = null;
            ImageCache.Shared.Clear();
            _pageStrip.Children.Clear();
            _emptyText.Visibility = Visibility.Visible;
            _fileText.Text = "";
            Title = "Manga Reader";

            if (sources.Length > 0) OpenPaths(sources);
            else MessageBox.Show(this, "缓存已清空。", "Manga Reader", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private Border BuildHintBar()
        {
            TextBlock t = new TextBlock();
            t.Text = "← → 翻页    空格 下一页    Home / End 首尾    Ctrl+滚轮 缩放    Tab 隐藏工具栏    F11 全屏    F1 帮助";
            t.Foreground = UI.C(140, 140, 150);
            t.FontSize = 12;
            t.HorizontalAlignment = HorizontalAlignment.Center;

            _hintBar = new Border();
            _hintBar.Background = new SolidColorBrush(Color.FromArgb(205, 22, 22, 28));
            _hintBar.BorderBrush = UI.C(60, 60, 70);
            _hintBar.BorderThickness = new Thickness(0, 1, 0, 0);
            _hintBar.VerticalAlignment = VerticalAlignment.Bottom;
            _hintBar.Padding = new Thickness(0, 5, 0, 5);
            _hintBar.Child = t;
            _hintBar.IsHitTestVisible = false;
            return _hintBar;
        }

        private Border BuildToast()
        {
            _toastText = new TextBlock();
            _toastText.Foreground = Brushes.White;
            _toastText.FontSize = 13;

            _toast = new Border();
            _toast.Background = new SolidColorBrush(Color.FromArgb(238, 30, 30, 38));
            _toast.BorderBrush = UI.C(96, 96, 112);
            _toast.BorderThickness = new Thickness(1);
            _toast.CornerRadius = new CornerRadius(6);
            _toast.Padding = new Thickness(16, 9, 16, 9);
            _toast.HorizontalAlignment = HorizontalAlignment.Center;
            _toast.VerticalAlignment = VerticalAlignment.Bottom;
            _toast.Margin = new Thickness(0, 0, 0, 54);
            _toast.Child = _toastText;
            _toast.Visibility = Visibility.Collapsed;
            _toast.IsHitTestVisible = false;
            return _toast;
        }

        private void Toast(string message)
        {
            if (_toast == null || _toastTimer == null) return;
            _toastText.Text = message;
            _toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private Border BuildBusyOverlay()
        {
            _busyText = new TextBlock();
            _busyText.Foreground = Brushes.White;
            _busyText.FontSize = 16;
            _busyText.HorizontalAlignment = HorizontalAlignment.Center;
            _busyText.Text = "正在载入…";

            _busyBar = new ProgressBar();
            _busyBar.Width = 360;
            _busyBar.Height = 8;
            _busyBar.Minimum = 0;
            _busyBar.Maximum = 100;
            _busyBar.Margin = new Thickness(0, 16, 0, 0);
            _busyBar.Foreground = UI.C(60, 150, 240);

            TextBlock sub = new TextBlock();
            sub.Text = "首次打开会把图片解压到缓存，之后再打开就是秒开";
            sub.Foreground = UI.C(150, 150, 160);
            sub.FontSize = 12;
            sub.Margin = new Thickness(0, 12, 0, 0);
            sub.HorizontalAlignment = HorizontalAlignment.Center;

            StackPanel sp = new StackPanel();
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(_busyText);
            sp.Children.Add(_busyBar);
            sp.Children.Add(sub);

            _busy = new Border();
            _busy.Background = new SolidColorBrush(Color.FromArgb(230, 10, 10, 12));
            _busy.Child = sp;
            _busy.Visibility = Visibility.Collapsed;
            return _busy;
        }

        private Border BuildHelpOverlay()
        {
            string[] lines = new string[]
            {
                "阅读方向",
                "  D                切换 从右往左 / 从左往右",
                "  ← →             按当前方向翻页",
                "",
                "翻页",
                "  空格 / PageDown   下一页       PageUp    上一页",
                "  Home / End        第一页 / 最后一页",
                "  单击画面          左半边 / 右半边翻页（随方向改变）",
                "",
                "显示",
                "  1 / 2 / 3         连续卷动 / 单页 / 双页",
                "  Ctrl + 滚轮       缩放（三种模式都可用）",
                "  Shift + 滚轮      连续卷动下左右平移（放大后看细节）",
                "  Tab / H           隐藏 / 显示工具栏，进入无干扰阅读",
                "  F11               全屏，全屏下移动鼠标可唤出工具栏",
                "  Esc               退出全屏 / 关闭本帮助",
                "  F1                显示 / 隐藏本帮助",
                "",
                "其它",
                "  直接把 .mobi 文件拖进窗口即可打开",
                "  一次选择多个文件会按顺序无缝连成一本书",
            };

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(34, 24, 40, 24);
            foreach (string s in lines)
            {
                TextBlock t = new TextBlock();
                t.Text = s;
                t.Foreground = (s.Length > 0 && s[0] != ' ') ? UI.C(110, 180, 255) : UI.C(225, 225, 235);
                t.FontSize = 14;
                t.LineHeight = 21;
                t.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, Segoe UI");
                sp.Children.Add(t);
            }

            _help = new Border();
            _help.Background = new SolidColorBrush(Color.FromArgb(246, 20, 20, 26));
            _help.BorderBrush = UI.C(70, 70, 84);
            _help.BorderThickness = new Thickness(1);
            _help.CornerRadius = new CornerRadius(8);
            _help.HorizontalAlignment = HorizontalAlignment.Center;
            _help.VerticalAlignment = VerticalAlignment.Center;
            _help.Child = sp;
            _help.Visibility = Visibility.Collapsed;
            _help.MouseLeftButtonDown += delegate { _help.Visibility = Visibility.Collapsed; };
            return _help;
        }

        // ------------------------------------------------------------------
        //  Layout maths
        // ------------------------------------------------------------------
        private static ScrollViewer FindScroll(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = VisualTreeHelper.GetChild(root, i);
                ScrollViewer sv = c as ScrollViewer;
                if (sv != null) return sv;
                sv = FindScroll(c);
                if (sv != null) return sv;
            }
            return null;
        }

        private double ViewportWidthOfList()
        {
            ScrollViewer sv = FindScroll(_list);
            if (sv != null && sv.ViewportWidth > 1) return sv.ViewportWidth;
            return Math.Max(200, _list.ActualWidth);
        }

        private void RefreshLayout(bool force)
        {
            double w;
            if (_mode == ViewMode.Continuous)
            {
                w = ViewportWidthOfList();
            }
            else
            {
                w = Math.Max(120, _pageScroll.ViewportWidth);
                _viewportHeight = Math.Max(120, _pageScroll.ViewportHeight);
            }
            if (w < 1) w = Math.Max(200, ActualWidth);
            _viewportWidth = w;
            if (force || Math.Abs(w - _layoutWidth) > 0.5) RecomputeLayout(w);
        }

        /// <summary>Continuous mode: page width as a multiple of the viewport width.</summary>
        private double ContinuousScale()
        {
            return _zoomMode == ZoomMode.Custom ? _zoom : 1.0;
        }

        private void RecomputeLayout(double width)
        {
            _layoutWidth = width;
            double scale = (_mode == ViewMode.Continuous) ? ContinuousScale() : 1.0;
            double pageWidth = Math.Max(40.0, width * scale);
            int n = _book.Pages.Count;
            _prefix = new double[n + 1];
            double acc = 0;
            for (int i = 0; i < n; i++)
            {
                _prefix[i] = acc;
                double h = pageWidth * _book.Pages[i].Aspect;
                if (h < 1) h = 1;
                acc += h;
            }
            _prefix[n] = acc;
            PageToImageConverter.DecodeWidth = (int)Math.Min(2400, Math.Max(760, pageWidth));
        }

        /// <summary>Force every realised page to re-run its bindings (new decode size).</summary>
        private void ReloadPageBindings()
        {
            System.Collections.IEnumerable src = _list.ItemsSource;
            if (src == null) return;
            _list.ItemsSource = null;
            _list.ItemsSource = src;
        }

        private void CenterHorizontally()
        {
            ScrollViewer sv = FindScroll(_list);
            if (sv == null) return;
            double max = Math.Max(0.0, sv.ExtentWidth - sv.ViewportWidth);
            sv.ScrollToHorizontalOffset(max / 2.0);
        }

        private int IndexAtOffset(double off)
        {
            int n = _book.Pages.Count;
            if (n == 0 || _prefix.Length < n + 1) return 0;
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_prefix[mid] <= off) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        private void OnListScroll(object sender, ScrollChangedEventArgs e)
        {
            if (_mode != ViewMode.Continuous) return;
            _scrollTimer.Stop();
            _scrollTimer.Start();
        }

        private void SyncIndexFromScroll()
        {
            if (_mode != ViewMode.Continuous || _book.Pages.Count == 0) return;
            ScrollViewer sv = FindScroll(_list);
            if (sv == null) return;
            int idx = IndexAtOffset(sv.VerticalOffset + 6);
            if (idx != _index) { _index = idx; UpdateLabels(); SyncChapterBox(); }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_book.Pages.Count == 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                double before = _layoutWidth;
                RefreshLayout(false);
                if (Math.Abs(_layoutWidth - before) > 0.5)
                {
                    if (_mode == ViewMode.Continuous) GoTo(_index);
                    else RenderSpread();
                }
            }));
        }

        // ------------------------------------------------------------------
        //  Book loading
        // ------------------------------------------------------------------
        /// <summary>Queue files to open as soon as the window is live.</summary>
        public void OpenOnLoad(string[] paths) { _pendingOpen = paths; }

        public void OpenPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            _busy.Visibility = Visibility.Visible;
            _busyBar.Value = 0;
            _busyText.Text = "正在载入…";

            BackgroundWorker bw = new BackgroundWorker();
            bw.WorkerReportsProgress = true;
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                try
                {
                    e.Result = BookLoader.Load((string[])e.Argument, BookLoader.DefaultCacheRoot,
                        delegate(double frac, string msg) { bw.ReportProgress((int)(frac * 100), msg); });
                }
                catch (Exception ex) { e.Result = ex; }
            };
            // BackgroundWorker only marshals by itself when a
            // SynchronizationContext exists, so post through the dispatcher.
            bw.ProgressChanged += delegate(object s, ProgressChangedEventArgs e)
            {
                int pct = e.ProgressPercentage;
                object state = e.UserState;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _busyBar.Value = pct;
                    if (state is string) _busyText.Text = (string)state;
                }));
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                object result = e.Result;
                Dispatcher.BeginInvoke(new Action(delegate { FinishOpen(result); }));
            };
            bw.RunWorkerAsync(paths);
        }

        private void FinishOpen(object result)
        {
            _busy.Visibility = Visibility.Collapsed;
            if (result is Exception)
            {
                Exception ex = (Exception)result;
                MessageBox.Show(this, ex.Message, "打不开这个文件", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ApplyBook((Book)result);
        }

        public void ApplyBook(Book b)
        {
            _book = b;
            _index = 0;
            ImageCache.Shared.Clear();
            _list.ItemsSource = null;
            _list.ItemsSource = _book.Pages;
            _emptyText.Visibility = _book.Pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _fileText.Text = _book.Title + (_book.Pages.Count > 0 ? "   ·   " + _book.Pages.Count + " 页" : "");
            Title = "Manga Reader - " + _book.Title;

            PopulateChapters();
            ContinuousZoom = ContinuousScale();
            UpdateLayout();
            RefreshLayout(true);
            ApplyModeVisibility();
            GoTo(0);
        }

        private void PopulateChapters()
        {
            _suppressUi = true;
            _chapterBox.Items.Clear();
            foreach (ChapterInfo c in _book.Chapters)
            {
                ComboBoxItem it = new ComboBoxItem();
                it.Content = c.Title + "  (" + c.PageCount + " 页)";
                it.Tag = c.FirstPage;
                _chapterBox.Items.Add(it);
            }
            bool multi = _book.Chapters.Count > 1;
            _chapterBox.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            _chapterLabel.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            if (multi) _chapterBox.SelectedIndex = 0;
            _suppressUi = false;
        }

        private void SyncChapterBox()
        {
            if (_book.Chapters.Count <= 1 || _suppressUi) return;
            int ci = 0;
            for (int i = 0; i < _book.Chapters.Count; i++)
                if (_book.Chapters[i].FirstPage <= _index) ci = i;
            if (_chapterBox.SelectedIndex != ci)
            {
                _suppressUi = true;
                _chapterBox.SelectedIndex = ci;
                _suppressUi = false;
            }
        }

        // ------------------------------------------------------------------
        //  Navigation
        // ------------------------------------------------------------------
        private void GoTo(int idx)
        {
            int n = _book.Pages.Count;
            if (n == 0) return;
            if (idx < 0) idx = 0;
            if (idx > n - 1) idx = n - 1;
            _index = idx;

            if (_mode == ViewMode.Continuous)
            {
                // Realise the target first: a recycling virtualising panel only
                // knows the extent of the items it has already measured.
                _list.ScrollIntoView(_book.Pages[idx]);
                _list.UpdateLayout();
                ScrollViewer sv = FindScroll(_list);
                if (sv != null && _prefix.Length > idx) sv.ScrollToVerticalOffset(_prefix[idx]);
            }
            else
            {
                RenderSpread();
            }
            UpdateLabels();
            SyncChapterBox();
        }

        private void NextPage()
        {
            GoTo(_index + (_mode == ViewMode.DoublePage ? 2 : 1));
        }

        private void PrevPage()
        {
            GoTo(_index - (_mode == ViewMode.DoublePage ? 2 : 1));
        }

        private void ClickLeft()
        {
            if (_dir == ReadDirection.Rtl) NextPage(); else PrevPage();
        }

        private void ClickRight()
        {
            if (_dir == ReadDirection.Rtl) PrevPage(); else NextPage();
        }

        private void ScrollBy(double dy)
        {
            ScrollViewer sv = FindScroll(_list);
            if (sv == null) return;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + dy);
        }

        private void RenderSpread()
        {
            _pageStrip.Children.Clear();
            int n = _book.Pages.Count;
            if (n == 0) return;

            if (_mode == ViewMode.SinglePage)
            {
                AddPageImage(_index, false);
                return;
            }

            int first = _index;
            int second = _index + 1;
            bool hasSecond = second < n;
            // Right to left: the earlier page sits on the right, like a manga.
            if (_dir == ReadDirection.Ltr)
            {
                AddPageImage(first, true);
                if (hasSecond) AddPageImage(second, true);
            }
            else
            {
                if (hasSecond) AddPageImage(second, true);
                AddPageImage(first, true);
            }
        }

        private void AddPageImage(int i, bool splitSpread)
        {
            if (i < 0 || i >= _book.Pages.Count) return;
            Image im = new Image();
            im.Stretch = Stretch.Uniform;
            im.Margin = new Thickness(0);
            im.HorizontalAlignment = HorizontalAlignment.Center;
            im.VerticalAlignment = VerticalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
            im.Source = LoadPageBitmap(i);
            im.Width = PageDisplayWidth(i, splitSpread);
            _pageStrip.Children.Add(im);
        }

        private double PageDisplayWidth(int i, bool splitSpread)
        {
            PageEntry pe = _book.Pages[i];
            double cellW = (splitSpread ? (_viewportWidth - 6) / 2 : _viewportWidth - 6);
            double cellH = _viewportHeight - 6;
            if (cellW < 40) cellW = 40;
            if (cellH < 40) cellH = 40;
            double nw = pe.Width > 0 ? pe.Width : 1000;
            double nh = pe.Height > 0 ? pe.Height : 1400;

            if (_zoomMode == ZoomMode.FitPage)
                return Math.Max(40, nw * Math.Min(cellW / nw, cellH / nh));
            if (_zoomMode == ZoomMode.FitWidth)
                return cellW;
            return Math.Max(60, nw * _zoom);
        }

        private ImageSource LoadPageBitmap(int i)
        {
            if (i < 0 || i >= _book.Pages.Count) return null;
            PageEntry pe = _book.Pages[i];
            int natural = pe.Width > 0 ? pe.Width : 1600;
            int w = (int)Math.Min(natural, Math.Max(900, _viewportWidth));
            return ImageCache.Shared.Get(pe.FilePath, w);
        }

        private void UpdateLabels()
        {
            int n = _book.Pages.Count;
            if (n == 0) { _pageText.Text = ""; return; }
            if (_mode == ViewMode.DoublePage)
            {
                int second = Math.Min(n, _index + 2);
                _pageText.Text = (_index + 1) + "-" + second + " / " + n;
            }
            else
            {
                _pageText.Text = (_index + 1) + " / " + n;
            }
        }

        // ------------------------------------------------------------------
        //  Modes, zoom, direction
        // ------------------------------------------------------------------
        private void SetMode(int i)
        {
            _mode = i == 0 ? ViewMode.Continuous : (i == 1 ? ViewMode.SinglePage : ViewMode.DoublePage);
            Settings.Set("mode", _mode.ToString());
            if (_mode == ViewMode.Continuous) ContinuousZoom = ContinuousScale();
            ApplyModeVisibility();
            UpdateLayout();
            RefreshLayout(true);
            GoTo(_index);
        }

        private void ApplyModeVisibility()
        {
            bool cont = _mode == ViewMode.Continuous;
            _list.Visibility = cont ? Visibility.Visible : Visibility.Collapsed;
            _pageScroll.Visibility = cont ? Visibility.Collapsed : Visibility.Visible;
            _zoomBox.ToolTip = cont
                ? "连续卷动模式：百分比相对视口宽度，100% 即适应宽度；超过 100% 可左右拖动查看"
                : "单页 / 双页模式：百分比相对图片原始尺寸";
        }

        private void SetZoom(int i)
        {
            if (i == 0) { _zoomMode = ZoomMode.FitPage; _zoom = 1.0; }
            else if (i == 1) { _zoomMode = ZoomMode.FitWidth; _zoom = 1.0; }
            else
            {
                _zoomMode = ZoomMode.Custom;
                double[] levels = new double[] { 0, 0, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0 };
                _zoom = levels[i];
            }
            Settings.Set("zoom", i.ToString(CultureInfo.InvariantCulture));

            if (_mode == ViewMode.Continuous)
            {
                ContinuousZoom = ContinuousScale();
                ImageCache.Shared.Clear();
                ReloadPageBindings();
                RecomputeLayout(_layoutWidth > 0 ? _layoutWidth : _viewportWidth);
                GoTo(_index);
                CenterHorizontally();
            }
            else
            {
                UpdateLayout();
                RefreshLayout(true);
                RenderSpread();
            }
        }

        private void ZoomStep(int dir)
        {
            int i = _zoomBox.SelectedIndex + dir;
            if (i < 0) i = 0;
            if (i > _zoomBox.Items.Count - 1) i = _zoomBox.Items.Count - 1;
            if (i == _zoomBox.SelectedIndex) return;
            _suppressUi = true;
            _zoomBox.SelectedIndex = i;
            _suppressUi = false;
            SetZoom(i);
        }

        private void ToggleDirection()
        {
            _dirBox.SelectedIndex = _dirBox.SelectedIndex == 0 ? 1 : 0;
        }

        // ------------------------------------------------------------------
        //  Fullscreen and chrome
        // ------------------------------------------------------------------
        private void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _savedState = WindowState;
                _savedStyle = WindowStyle;
                _savedResize = ResizeMode;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
                _fullscreen = true;
                _fullBtn.Content = "退出全屏 (Esc)";
                _autoHidden = false;
                UpdateChrome();
                _hideTimer.Stop();
                if (!_chromeHidden) _hideTimer.Start();
            }
            else
            {
                _fullscreen = false;
                WindowStyle = _savedStyle;
                ResizeMode = _savedResize;
                WindowState = WindowState.Normal;
                WindowState = _savedState;
                _fullBtn.Content = "全屏 (F11)";
                _autoHidden = false;
                _hideTimer.Stop();
                UpdateChrome();
            }
            Settings.Set("fullscreen", _fullscreen ? "1" : "0");
        }

        /// <summary>Single place that decides whether the toolbars show.</summary>
        private void UpdateChrome()
        {
            bool show = !_chromeHidden && !_autoHidden;
            _toolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _hintBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Cursor = (_fullscreen && !show) ? Cursors.None : Cursors.Arrow;
        }

        private void ShowChrome()
        {
            if (_chromeHidden) return;
            _autoHidden = false;
            UpdateChrome();
        }

        private void HideChrome()
        {
            if (_chromeHidden) return;
            _autoHidden = true;
            UpdateChrome();
        }

        /// <summary>Hide / show the toolbar manually (Tab or H).</summary>
        private void ToggleChrome()
        {
            _chromeHidden = !_chromeHidden;
            _autoHidden = false;
            Settings.Set("hidechrome", _chromeHidden ? "1" : "0");
            UpdateChrome();
            Toast(_chromeHidden ? "工具栏已隐藏 · 按 Tab / H 或 Esc 恢复" : "工具栏已显示");
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_chromeHidden || !_fullscreen) return;
            if (_autoHidden) { _autoHidden = false; UpdateChrome(); }
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        // ------------------------------------------------------------------
        //  Input
        // ------------------------------------------------------------------
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_help.Visibility == Visibility.Visible && e.Key != Key.F1 && e.Key != Key.Escape)
                return;

            // Let combo boxes and buttons keep their own key handling.
            if (Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is ComboBoxItem)
            {
                if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down) return;
            }
            if (e.Key == Key.Space && Keyboard.FocusedElement is Button) return;

            switch (e.Key)
            {
                case Key.F11: ToggleFullscreen(); e.Handled = true; break;
                case Key.F1:
                    _help.Visibility = _help.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    e.Handled = true; break;
                case Key.Tab:
                case Key.H: ToggleChrome(); e.Handled = true; break;
                case Key.Escape:
                    if (_help.Visibility == Visibility.Visible) _help.Visibility = Visibility.Collapsed;
                    else if (_chromeHidden) ToggleChrome();
                    else if (_fullscreen) ToggleFullscreen();
                    e.Handled = true; break;
                case Key.D: ToggleDirection(); e.Handled = true; break;
                case Key.D1: _modeBox.SelectedIndex = 0; e.Handled = true; break;
                case Key.D2: _modeBox.SelectedIndex = 1; e.Handled = true; break;
                case Key.D3: _modeBox.SelectedIndex = 2; e.Handled = true; break;
                case Key.Left:
                    if (_dir == ReadDirection.Rtl) NextPage(); else PrevPage();
                    e.Handled = true; break;
                case Key.Right:
                    if (_dir == ReadDirection.Rtl) PrevPage(); else NextPage();
                    e.Handled = true; break;
                case Key.Up:
                    if (_mode == ViewMode.Continuous) ScrollBy(-180); else PrevPage();
                    e.Handled = true; break;
                case Key.Down:
                    if (_mode == ViewMode.Continuous) ScrollBy(180); else NextPage();
                    e.Handled = true; break;
                case Key.PageDown:
                case Key.Space:
                case Key.Enter: NextPage(); e.Handled = true; break;
                case Key.PageUp:
                case Key.Back: PrevPage(); e.Handled = true; break;
                case Key.Home: GoTo(0); e.Handled = true; break;
                case Key.End: GoTo(_book.Pages.Count - 1); e.Handled = true; break;
                case Key.Add:
                case Key.OemPlus: ZoomStep(1); e.Handled = true; break;
                case Key.Subtract:
                case Key.OemMinus: ZoomStep(-1); e.Handled = true; break;
                case Key.D0: _zoomBox.SelectedIndex = 0; e.Handled = true; break;
            }
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ZoomStep(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
                return;
            }
            if (_mode == ViewMode.Continuous)
            {
                // Zoomed in? Shift + wheel pans sideways.
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    ScrollViewer side = FindScroll(_list);
                    if (side != null)
                    {
                        side.ScrollToHorizontalOffset(side.HorizontalOffset - e.Delta);
                        e.Handled = true;
                    }
                }
                return;   // otherwise the list scrolls itself
            }
            if (_pageScroll.ScrollableHeight > 1) return;
            if (e.Delta > 0) PrevPage(); else NextPage();
            e.Handled = true;
        }

        private void OnOpen(object sender, RoutedEventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Title = "打开漫画文件";
            d.Filter = "MOBI / AZW 漫画 (*.mobi;*.azw;*.azw3;*.prc)|*.mobi;*.azw;*.azw3;*.prc|所有文件 (*.*)|*.*";
            d.Multiselect = true;
            if (d.ShowDialog(this) == true) OpenPaths(d.FileNames);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            List<string> ok = new List<string>();
            foreach (string f in files)
            {
                if (Directory.Exists(f))
                {
                    string[] inner = Directory.GetFiles(f);
                    Array.Sort(inner, StringComparer.OrdinalIgnoreCase);
                    foreach (string g in inner) if (IsBook(g)) ok.Add(g);
                }
                else if (IsBook(f)) ok.Add(f);
            }
            if (ok.Count == 0)
            {
                MessageBox.Show(this, "没有找到 MOBI / AZW3 漫画文件。", "Manga Reader",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenPaths(ok.ToArray());
        }

        public static bool IsBook(string path)
        {
            string e = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return e == ".mobi" || e == ".azw" || e == ".azw3" || e == ".prc";
        }

        // ------------------------------------------------------------------
        //  Preferences
        // ------------------------------------------------------------------
        private void LoadPrefs()
        {
            _suppressUi = true;
            _dir = Settings.Get("dir", "Rtl") == "Ltr" ? ReadDirection.Ltr : ReadDirection.Rtl;
            _dirBox.SelectedIndex = _dir == ReadDirection.Rtl ? 0 : 1;

            string m = Settings.Get("mode", "Continuous");
            _mode = m == "SinglePage" ? ViewMode.SinglePage : (m == "DoublePage" ? ViewMode.DoublePage : ViewMode.Continuous);
            _modeBox.SelectedIndex = _mode == ViewMode.Continuous ? 0 : (_mode == ViewMode.SinglePage ? 1 : 2);

            int z = 0;
            int.TryParse(Settings.Get("zoom", "0"), out z);
            if (z < 0 || z > 8) z = 0;
            _zoomBox.SelectedIndex = z;
            SetZoom(z);
            _chromeHidden = Settings.Get("hidechrome", "0") == "1";
            _suppressUi = false;
            UpdateChrome();

            if (Settings.Get("fullscreen", "0") == "1")
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { ToggleFullscreen(); }));
        }

        // ------------------------------------------------------------------
        //  Offscreen rendering (used by the built in --render-ui self check)
        // ------------------------------------------------------------------
        public void RenderConfigure(int modeIndex, int dirIndex, int pageIndex, int zoomIndex)
        {
            _chromeHidden = false;                 // never screenshot a bare screen
            _autoHidden = false;
            UpdateChrome();

            _suppressUi = true;
            _modeBox.SelectedIndex = modeIndex;
            _dirBox.SelectedIndex = dirIndex;
            _zoomBox.SelectedIndex = zoomIndex;
            _suppressUi = false;
            _mode = modeIndex == 0 ? ViewMode.Continuous : (modeIndex == 1 ? ViewMode.SinglePage : ViewMode.DoublePage);
            _dir = dirIndex == 0 ? ReadDirection.Rtl : ReadDirection.Ltr;
            SetZoom(zoomIndex);
            ApplyModeVisibility();
            UpdateLayout();
            RefreshLayout(true);
            GoTo(pageIndex);
        }

        public void RenderTo(string file, int w, int h, double extraOffset)
        {
            Width = w;
            Height = h;
            _root.Measure(new Size(w, h));
            _root.Arrange(new Rect(0, 0, w, h));
            _root.UpdateLayout();
            RefreshLayout(true);
            System.Text.StringBuilder diag = new System.Text.StringBuilder();
            if (_mode == ViewMode.Continuous)
            {
                GoTo(_index);
                _list.UpdateLayout();
                if (extraOffset != 0)
                {
                    ScrollViewer sv = FindScroll(_list);
                    if (sv != null) sv.ScrollToVerticalOffset(sv.VerticalOffset + extraOffset);
                }
                _list.UpdateLayout();
                ScrollViewer s2 = FindScroll(_list);
                if (s2 != null)
                    diag.AppendLine("offset=" + s2.VerticalOffset + " extent=" + s2.ExtentHeight +
                                    " viewport=" + s2.ViewportHeight + " scrollable=" + s2.ScrollableHeight +
                                    " index=" + _index + " prefixTarget=" + (_prefix.Length > _index ? _prefix[_index] : -1));
            }
            else RenderSpread();
            _root.UpdateLayout();

            RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(_root);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(file)) enc.Save(fs);
            if (diag.Length > 0) File.WriteAllText(file + ".info.txt", diag.ToString());
        }
    }

    // =====================================================================
    //  Entry point
    // =====================================================================
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // Headless self check modes: never show UI, report failures in
                // a side-car file so a batch run can never block on a dialog.
                if (args.Length >= 3 && args[0] == "--selftest")
                {
                    int rc = 0;
                    try { RunSelfTest(args[1], args[2]); }
                    catch (Exception ex) { File.WriteAllText(args[2] + ".err.txt", ex.ToString()); rc = 1; }
                    Environment.Exit(rc);
                    return;
                }
                if (args.Length >= 3 && args[0] == "--render-ui")
                {
                    int rc = 0;
                    try { RunRenderUi(args); }
                    catch (Exception ex) { File.WriteAllText(args[2] + ".err.txt", ex.ToString()); rc = 1; }
                    Environment.Exit(rc);
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException +=
                    delegate(object s, UnhandledExceptionEventArgs e) { LogCrash(e.ExceptionObject as Exception); };

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    LogCrash(e.Exception);
                    e.Handled = true;
                    MessageBox.Show(e.Exception.Message, "Manga Reader 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
                };

                MainWindow win = new MainWindow();
                List<string> files = new List<string>();
                foreach (string a in args)
                    if (File.Exists(a) && MainWindow.IsBook(a)) files.Add(a);
                if (files.Count > 0) win.OpenOnLoad(files.ToArray());
                app.Run(win);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Manga Reader", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void LogCrash(Exception ex)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaReader");
                Directory.CreateDirectory(dir);
                File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    (ex == null ? "(no exception object)" : ex.ToString()) + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        private static void RunSelfTest(string bookPath, string reportPath)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            using (MobiFile mf = new MobiFile(bookPath))
            {
                sb.AppendLine("title=" + mf.Title);
                sb.AppendLine("version=" + mf.Version);
                sb.AppendLine("images=" + mf.ImageRecords.Count);
                sb.AppendLine("cover=" + mf.CoverOrdinal);
            }
            Book b = BookLoader.Load(new string[] { bookPath },
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mr_selftest"), null);
            sb.AppendLine("pages=" + b.Pages.Count);
            if (b.Pages.Count > 0)
                sb.AppendLine("page0=" + b.Pages[0].Width + "x" + b.Pages[0].Height);
            File.WriteAllText(reportPath, sb.ToString());
        }

        private static void RunRenderUi(string[] args)
        {
            string bookPath = args[1];
            string outPng = args[2];
            int w = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1280;
            int h = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 840;
            string mode = args.Length > 5 ? args[5] : "cont";
            string dir = args.Length > 6 ? args[6] : "rtl";
            int page = args.Length > 7 ? int.Parse(args[7], CultureInfo.InvariantCulture) : 0;
            double extra = args.Length > 8 ? double.Parse(args[8], CultureInfo.InvariantCulture) : 0;
            int zoomIdx = args.Length > 9 ? int.Parse(args[9], CultureInfo.InvariantCulture) : 0;

            Application app = new Application();
            MainWindow win = new MainWindow();
            Book b = BookLoader.Load(bookPath.Split(';'),
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mr_selftest"), null);
            win.ApplyBook(b);
            win.RenderConfigure(mode == "cont" ? 0 : (mode == "single" ? 1 : 2), dir == "rtl" ? 0 : 1, page, zoomIdx);
            win.RenderTo(outPng, w, h, extra);
        }
    }
}
