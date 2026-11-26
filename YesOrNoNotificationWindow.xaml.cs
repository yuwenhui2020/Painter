using System;
using System.Windows;
using System.Windows.Controls;
using Painter.Helpers;

namespace Painter
{
    /// <summary>
    /// Interaction logic for RestoreHiddenSlidesWindow.xaml
    /// </summary>
    public partial class YesOrNoNotificationWindow : Window
    {
        private readonly Action _yesAction;
        private readonly Action _noAction;
        private readonly bool _clearHiddenSlidesFlag;

        /// <param name="clearHiddenSlidesFlag">仅"取消隐藏幻灯片"对话关闭时才重置对应标志；
        /// 本窗口类也用于"上次播放页"提醒，不能无条件清掉属于另一路的状态。</param>
        public YesOrNoNotificationWindow(string text, Action yesAction = null, Action noAction = null, bool clearHiddenSlidesFlag = false)
        {
            _clearHiddenSlidesFlag = clearHiddenSlidesFlag;
            _yesAction = yesAction;
            _noAction = noAction;
            InitializeComponent();
            WindowBackdrop.Apply(this);
            Label.Content = text;
        }

        private void ButtonYes_Click(object sender, RoutedEventArgs e)
        {
            if (_yesAction == null)
            {
                Close();
                return;
            }

            _yesAction.Invoke();
            Close();

        }

        private void ButtonNo_Click(object sender, RoutedEventArgs e)
        {
            if (_noAction == null)
            {
                Close();
                return;
            }

            _noAction.Invoke();
            Close();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (_clearHiddenSlidesFlag)
                MainWindow.IsShowingRestoreHiddenSlidesWindow = false;
        }
    }
}