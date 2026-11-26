using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Painter
{
    /// <summary>
    /// Interaction logic for RandWindow.xaml
    /// </summary>
    public partial class RandWindow : Window
    {
        public RandWindow()
        {
            InitializeComponent();
            _rollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(RollIntervalMs), DispatcherPriority.Render, RollTimer_Tick, Dispatcher);
            _rollTimer.Stop();
            // 重新打开（从隐藏恢复）时重载名单，保证桌面名单改动即时生效
            Activated += (s, e) => LoadNames();
            Closed += (s, e) => { _delayTimer?.Stop(); StopRoll(); };
        }

        public RandWindow(bool IsAutoClose) : this()
        {
            isAutoClose = IsAutoClose;
            // 快捷“抽一人”：名单加载后自动滚动约 2 秒，锁定结果再短暂停留
            Loaded += (s, e) => ScheduleOnce(300, StartRoll);
        }

        public static int randSeed = 0;

        // 共享 Random 并加锁：避免快速连点时时间种子相同抽出完全相同的一串号码。
        private static readonly Random _random = new Random();
        private static readonly object _randomLock = new object();

        private static int NextRandom(int minValue, int maxValue)
        {
            lock (_randomLock) { return _random.Next(minValue, maxValue); }
        }

        // 动画与状态全部留在 UI 线程：不再跨线程改集合，也不再向已关闭窗口 Invoke。
        private const int RollIntervalMs = 100;      // 每秒跳动 10 个不同姓名
        private const int AutoRollTicks = 20;        // 快捷模式滚动 2 秒
        private const int AutoCloseDelayMs = 2500;   // 快捷模式结果停留 2.5 秒

        private readonly List<int> _picked = new List<int>();          // 本窗口已抽中的 0 基索引
        private readonly HashSet<int> _rollingSeen = new HashSet<int>();
        private DispatcherTimer _rollTimer;
        private DispatcherTimer _delayTimer;
        private bool _isRolling;
        private int _rollTicks;
        private int _lastRolledIndex = -1;
        private string _rosterSignature = "";

        public bool isAutoClose = false;

        public int TotalCount = 1;
        public int PeopleCount = 60;
        public List<string> Names = new List<string>();

        private int RosterSize => Math.Max(1, PeopleCount);

        /// <summary>一次性延时任务；重复调用会取消上一个，避免窗口关闭后仍有待触发回调。</summary>
        private void ScheduleOnce(int delayMs, Action action)
        {
            _delayTimer?.Stop();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            EventHandler tick = null;
            tick = (s, e) => { timer.Stop(); timer.Tick -= tick; try { action(); } catch { } };
            timer.Tick += tick;
            _delayTimer = timer;
            timer.Start();
        }

        private void BorderBtnAdd_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (TotalCount >= PeopleCount) return;
            TotalCount++;
            LabelNumberCount.Content = TotalCount.ToString();
            SymbolIconStart.Glyph = "People";
        }

        private void BorderBtnMinus_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (TotalCount < 2) return;
            TotalCount--;
            LabelNumberCount.Content = TotalCount.ToString();
            if (TotalCount == 1)
            {
                SymbolIconStart.Glyph = "Contact";
            }
        }

        private void BorderBtnRand_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e != null && e.ChangedButton != MouseButton.Left) return;
            if (_isRolling) StopRoll(); else StartRoll();
        }

        private void LabelOutput_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e != null && e.ChangedButton != MouseButton.Left) return;
            // 已锁定姓名时点击 = 发起新一轮点名；动画中点击 = 立即停住
            if (_isRolling) StopRoll(); else StartRoll();
        }

        private void StartRoll()
        {
            if (_isRolling || !IsLoaded) return;
            if (!Dispatcher.CheckAccess())
            {
                try { Dispatcher.BeginInvoke(new Action(StartRoll)); } catch { }
                return;
            }

            _isRolling = true;
            _rollTicks = 0;
            _rollingSeen.Clear();
            _delayTimer?.Stop();

            LabelOutput2.Visibility = Visibility.Collapsed;
            LabelOutput3.Visibility = Visibility.Collapsed;
            BorderBtnRandCover.Visibility = Visibility.Visible;
            TextBlockRandAction.Text = "停住";

            RollOnce();
            _rollTimer.Start();
        }

        private void RollTimer_Tick(object sender, EventArgs e)
        {
            if (!_isRolling) return;
            try
            {
                _rollTicks++;
                // 快捷“抽一人”模式没有人工点击，滚动固定时长后自动锁定
                if (isAutoClose && _rollTicks >= AutoRollTicks) { StopRoll(); return; }
                RollOnce();
            }
            catch
            {
                // 定时器回调里的异常绝不能穿过 Dispatcher，否则整个进程退出
                _rollTimer.Stop();
                _isRolling = false;
            }
        }

        private void RollOnce()
        {
            int index = NextCandidate();
            if (index < 0) { LabelOutput.Content = "名单为空"; return; }
            _rollingSeen.Add(index);
            _lastRolledIndex = index;
            LabelOutput.Content = NameAt(index);
        }

        private void StopRoll()
        {
            _rollTimer?.Stop();
            if (!_isRolling) return;
            _isRolling = false;
            BorderBtnRandCover.Visibility = Visibility.Collapsed;
            TextBlockRandAction.Text = "抽奖";

            // 停住时屏幕上的姓名即本次结果，保持显示；后续轮次不再重复抽到同一人。
            if (_lastRolledIndex >= 0 && _lastRolledIndex < RosterSize && !_picked.Contains(_lastRolledIndex))
                _picked.Add(_lastRolledIndex);

            int extra = Math.Max(0, Math.Min(TotalCount, RosterSize) - 1);
            for (int i = 0; i < extra; i++)
            {
                int index = NextCandidate();
                if (index < 0) break;
                _picked.Add(index);
            }
            RenderPicked();

            if (isAutoClose) ScheduleOnce(AutoCloseDelayMs, () => { try { Close(); } catch { } });
        }

        /// <summary>优先抽“未抽中且本轮未闪过”的姓名；闪烁池耗尽时允许重复闪现，但不清空已抽结果。</summary>
        private int NextCandidate()
        {
            int size = RosterSize;
            var candidates = new List<int>(size);
            for (int i = 0; i < size; i++)
                if (!_picked.Contains(i) && !_rollingSeen.Contains(i)) candidates.Add(i);
            if (candidates.Count > 0) return candidates[NextRandom(0, candidates.Count)];

            // 名单很短、动画时间较长时本轮闪烁会覆盖所有人：继续闪未抽中的人，历史结果保留
            candidates.Clear();
            for (int i = 0; i < size; i++)
                if (!_picked.Contains(i)) candidates.Add(i);
            if (candidates.Count > 0) return candidates[NextRandom(0, candidates.Count)];

            // 全部同学都抽过了，才真正开启新一轮
            _picked.Clear();
            _rollingSeen.Clear();
            _lastRolledIndex = -1;
            for (int i = 0; i < size; i++) candidates.Add(i);
            return candidates.Count == 0 ? -1 : candidates[NextRandom(0, candidates.Count)];
        }

        private string NameAt(int index)
        {
            if (Names.Count > 0) return index >= 0 && index < Names.Count ? Names[index] : "";
            return (index + 1).ToString();
        }

        private void RenderPicked()
        {
            int n = _picked.Count;
            if (n == 0)
            {
                LabelOutput.Content = "名单为空";
                LabelOutput2.Visibility = Visibility.Collapsed;
                LabelOutput3.Visibility = Visibility.Collapsed;
                return;
            }

            int first = n <= 5 ? n : n <= 10 ? (n + 1) / 2 : (n + 1) / 3;
            int second = n <= 5 ? n : n <= 10 ? n : (n + 1) * 2 / 3;
            int columns = n <= 5 ? 1 : n <= 10 ? 2 : 3;

            LabelOutput.Content = BuildColumn(0, first);
            LabelOutput2.Visibility = columns >= 2 ? Visibility.Visible : Visibility.Collapsed;
            LabelOutput2.Content = columns >= 2 ? BuildColumn(first, second - first) : "";
            LabelOutput3.Visibility = columns >= 3 ? Visibility.Visible : Visibility.Collapsed;
            LabelOutput3.Content = columns >= 3 ? BuildColumn(second, n - second) : "";
        }

        private string BuildColumn(int start, int count)
        {
            var sb = new StringBuilder();
            for (int i = start; i < start + count && i < _picked.Count; i++)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(NameAt(_picked[i]));
            }
            return sb.ToString().Trim();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadNames();
        }

        /// <summary>
        /// 载入点名器名单。默认优先使用桌面上的“班级学生名单.txt”，
        /// 找不到时回退到旧逻辑的程序目录 Names.txt；二者皆无则以 60 人为默认。
        /// 每次窗口被激活（重新打开）都会重载，保证桌面名单改动后一点就生效。
        /// </summary>
        private void LoadNames()
        {
            try
            {
                Names = new List<string>();
                string namesFile = null;

                // 优先级：① 导入的名单（程序目录 Names.txt，导入后默认一直使用，防学生私自替换）
                //          ② 桌面“班级学生名单.txt”    ③ 公共桌面同名文件
                if (File.Exists(App.RootPath + "Names.txt"))
                {
                    namesFile = App.RootPath + "Names.txt";
                }
                else
                {
                    string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "班级学生名单.txt");
                    if (File.Exists(desktop))
                    {
                        namesFile = desktop;
                    }
                    else
                    {
                        string commonDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "班级学生名单.txt");
                        if (File.Exists(commonDesktop)) namesFile = commonDesktop;
                    }
                }

                if (namesFile != null)
                {
                    string[] fileNames = File.ReadAllLines(namesFile);
                    string[] replaces = File.Exists(App.RootPath + "Replace.txt")
                        ? File.ReadAllLines(App.RootPath + "Replace.txt")
                        : new string[0];

                    //Fix emtpy lines
                    foreach (string str in fileNames)
                    {
                        string s = str;
                        //Make replacement
                        foreach (string replace in replaces)
                        {
                            if (replace.IndexOf("-->") >= 0 && s == Strings.Left(replace, replace.IndexOf("-->")))
                            {
                                s = Strings.Mid(replace, replace.IndexOf("-->") + 4);
                            }
                        }

                        if (s != "") Names.Add(s);
                    }
                }

                PeopleCount = Names.Count > 0 ? Names.Count : 60;
                TextBlockPeopleCount.Text = Names.Count > 0 ? Names.Count.ToString() : "导入名单后开始";

                // 只有名单真的变了才清空已抽结果：窗口重新激活时不会误清。
                string signature = Names.Count + ":" + string.Join("\u0001", Names);
                if (signature != _rosterSignature)
                {
                    _rosterSignature = signature;
                    _picked.Clear();
                    _rollingSeen.Clear();
                    _lastRolledIndex = -1;
                }
                ShowRosterInfo();
            }
            catch (Exception)
            {
                // 名单文件被占用/损坏时退回默认人数，绝不让异常冲到 UI 事件循环导致闪退。
                Names = new List<string>();
                PeopleCount = 60;
                TextBlockPeopleCount.Text = "导入名单后开始";
                _picked.Clear();
                _rollingSeen.Clear();
                _lastRolledIndex = -1;
                _rosterSignature = "";
                try { ShowRosterInfo(); } catch { }
            }
        }

        /// <summary>
        /// 显示名单来源与导入时间（年-月-日）。导入时间可让家长/老师核对名单是否被人私自替换：
        /// 一旦被改动或改用桌面文件，导入时间会缺失或变化。
        /// </summary>
        private void ShowRosterInfo()
        {
            string date = "", file = "";
            try
            {
                date = MainWindow.Settings?.Roster?.ImportDate ?? "";
                file = MainWindow.Settings?.Roster?.FileName ?? "";
            }
            catch { }
            string source;
            if (!string.IsNullOrEmpty(date))
            {
                source = "导入时间：" + date;
                if (!string.IsNullOrEmpty(file)) source += "（" + file + "）";
            }
            else if (File.Exists(App.RootPath + "Names.txt"))
            {
                source = "已载入本地名单（未记录导入时间）";
            }
            else
            {
                source = "尚未导入名单（使用默认 60 人 / 桌面文件）";
            }
            TextBlockRosterInfo.Text = source + "　共 " + PeopleCount + " 人　·　点击姓名可再次抽取";
        }

        /// <summary>
        /// 导入名单：把外部 .txt（一行一个姓名）写入程序目录 Names.txt，
        /// 记录导入时间与原始文件名，并持久化到 Settings.json。
        /// 之后所有点名功能（抽奖 / 抽一人）都会默认使用这份名单。
        /// </summary>
        private void BorderBtnImport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                Title = "导入班级名单（一行一个姓名）"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (lines.Count == 0)
                {
                    System.Windows.MessageBox.Show("该文件没有可读的姓名，请确认是“一行一个姓名”的文本文件。", "导入失败");
                    return;
                }
                string dest = Path.Combine(App.RootPath, "Names.txt");
                File.WriteAllLines(dest, lines, Encoding.UTF8);
                MainWindow.Settings.Roster.ImportDate = DateTime.Now.ToString("yyyy-MM-dd");
                MainWindow.Settings.Roster.FileName = Path.GetFileName(dlg.FileName);
                MainWindow.SaveSettingsToFile();
                LoadNames();
                System.Windows.MessageBox.Show("已导入名单：" + lines.Count + " 人。", "导入成功");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("导入失败：" + ex.Message, "导入失败");
            }
        }

        private void BorderBtnHelp_MouseUp(object sender, MouseButtonEventArgs e)
        {
            //MessageBox.Show("如需显示姓名，请在程序目录下新建 Names.txt，并将姓名输入，一行一个。");
            try
            {
                new NamesInputWindow().ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("打开名单编辑窗口失败：" + ex.Message, "名单导入");
            }
            LoadNames();
        }

        private void BtnClose_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // 隐藏前停掉动画与延时回调，避免后台继续刷新不可见窗口或在关闭后触发
            _delayTimer?.Stop();
            StopRoll();
            // 用隐藏代替关闭：下次唤起直接复用本窗口实例，免去重新解析 XAML 与读盘的延迟
            Hide();
        }
    }
}
